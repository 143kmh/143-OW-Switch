using Microsoft.Win32;

namespace Excluder.App;

public static class OverwatchLocator
{
    public static bool IsValid(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
            Path.GetFileName(path).Equals("Overwatch.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path); }
        catch (ArgumentException) { return false; }
    }

    public static string? Find()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        { roots.Add(Path.Combine(folder, "Overwatch")); roots.Add(Path.Combine(folder, "Battle.net", "Overwatch")); }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            foreach (var relative in new[] { "Overwatch", @"Games\Overwatch", @"Battle.net\Overwatch", @"Program Files (x86)\Overwatch", @"SteamLibrary\steamapps\common\Overwatch", @"Games\Steam\steamapps\common\Overwatch" })
                roots.Add(Path.Combine(drive.RootDirectory.FullName, relative));
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if ((app?.GetValue("DisplayName") as string)?.StartsWith("Overwatch", StringComparison.OrdinalIgnoreCase) == true &&
                            app?.GetValue("InstallLocation") is string location && location.Length > 0) roots.Add(location);
                    }
                }
                catch (Exception error) { Log.Write("Executable discovery: " + error.Message); }
            }
        return roots.SelectMany(root => new[] { Path.Combine(root, "_retail_", "Overwatch.exe"), Path.Combine(root, "Overwatch.exe") }).FirstOrDefault(IsValid);
    }
}
