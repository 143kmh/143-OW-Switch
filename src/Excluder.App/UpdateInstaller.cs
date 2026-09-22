using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Excluder.Core;

namespace Excluder.App;

public sealed record UpdatePlan(int ParentId, string Target, string OldHash, string NewHash, string Nonce);

public static class UpdateInstaller
{
    // Elevated executable staging must not be writable by ordinary processes in %TEMP%.
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "143 OW Switch Updates");
    public static async Task<string> Prepare(ReleaseInfo release, CancellationToken token)
    {
        if (!App.Elevated) throw new UnauthorizedAccessException("Administrator permission is required.");
        var checksum = Integrity.ParseChecksum(Encoding.UTF8.GetString(await RemoteServices.Fetch(release.ChecksumUrl, 4096, token)));
        var bytes = await RemoteServices.Fetch(release.ExecutableUrl, 256 * 1024 * 1024, token);
        Directory.CreateDirectory(Root);
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe update directory.");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(admins);
        foreach (var sid in new[] { admins, system }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var payload = Path.Combine(directory, "143OWSwitch.exe");
            await File.WriteAllBytesAsync(payload, bytes, token); Integrity.Verify(payload, checksum);
            var version = FileVersionInfo.GetVersionInfo(payload);
            if (version.ProductName != "143 OW Switch" || version.FileMajorPart != release.Version.Major ||
                version.FileMinorPart != release.Version.Minor || version.FileBuildPart != release.Version.Patch)
                throw new InvalidDataException("Update verification failed.");
            var current = Environment.ProcessPath!;
            File.Copy(current, Path.Combine(directory, "UpdateHost.exe"));
            var plan = new UpdatePlan(Environment.ProcessId, current, Integrity.Hash(current), checksum, Guid.NewGuid().ToString("N"));
            AtomicFile.Write(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan));
            return directory;
        }
        catch { Directory.Delete(directory, true); throw; }
    }
    public static void Launch(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "UpdateHost.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--install-update"); start.ArgumentList.Add(directory);
        _ = Process.Start(start) ?? throw new IOException("Could not start update helper.");
    }
    private static string ValidateDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid update staging path.");
        return full;
    }
    // Runs before WPF/single-instance startup in a copy of the current executable.
    public static async Task Install(string directory)
    {
        UpdatePlan? plan = null;
        Process? child = null;
        try
        {
            directory = ValidateDirectory(directory);
            if (!string.Equals(Environment.ProcessPath, Path.Combine(directory, "UpdateHost.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected update helper location.");
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(directory, "plan.json")))!;
            if (!Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(plan.Nonce, "N", out _)) throw new InvalidDataException("Invalid update plan.");
            try { using var parent = Process.GetProcessById(plan.ParentId); if (!parent.WaitForExit(30000)) throw new IOException("Current application did not exit."); }
            catch (ArgumentException) { }
            var payload = Path.Combine(directory, "143OWSwitch.exe");
            var backup = await ExecutableSwap.Install(payload, plan.Target, plan.OldHash, plan.NewHash, plan.Nonce, async () =>
            {
                try
                {
                    var start = new ProcessStartInfo(plan.Target) { UseShellExecute = true };
                    start.ArgumentList.Add("--update-receipt"); start.ArgumentList.Add(directory);
                    child = Process.Start(start) ?? throw new IOException("New version could not start.");
                    var receipt = Path.Combine(directory, "started");
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30) && !File.Exists(receipt) && !child.HasExited) await Task.Delay(250);
                    if (!File.Exists(receipt) || File.ReadAllText(receipt) != plan.Nonce) throw new IOException("New version did not acknowledge startup.");
                    AtomicFile.Write(Path.Combine(directory, "complete"), Environment.ProcessId.ToString());
                }
                catch
                {
                    if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(10000); }
                    throw;
                }
            });
            try { File.Delete(backup); } catch (Exception cleanup) { Log.Write(cleanup.ToString()); }
        }
        catch (Exception error)
        {
            Log.Write("Couldn't install update: " + error);
            if (plan != null && File.Exists(plan.Target) && Integrity.Hash(plan.Target) == plan.OldHash)
            {
                try { Process.Start(new ProcessStartInfo(plan.Target, "--update-failed") { UseShellExecute = true }); } catch { }
            }
        }
        finally { child?.Dispose(); }
    }
    public static async Task AcknowledgeAndClean(string directory)
    {
        try
        {
            directory = ValidateDirectory(directory);
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(directory, "plan.json")))!;
            if (!string.Equals(Environment.ProcessPath, plan.Target, StringComparison.OrdinalIgnoreCase)) return;
            Integrity.Verify(Environment.ProcessPath!, plan.NewHash);
            AtomicFile.Write(Path.Combine(directory, "started"), plan.Nonce);
            var complete = Path.Combine(directory, "complete");
            for (int i = 0; i < 120 && !File.Exists(complete); i++) await Task.Delay(500);
            if (!File.Exists(complete)) return;
            if (int.TryParse(File.ReadAllText(complete), out var pid))
            { try { using var helper = Process.GetProcessById(pid); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch (ArgumentException) { } }
            Directory.Delete(directory, true);
        }
        catch (Exception error) { Log.Write("Update cleanup: " + error); }
    }
}
