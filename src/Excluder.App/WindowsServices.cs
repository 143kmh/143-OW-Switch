using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Excluder.Core;

namespace Excluder.App;

public sealed class WindowsFirewall : IFirewallStore
{
    private static dynamic Create(string id) => Activator.CreateInstance(Type.GetTypeFromProgID(id)
        ?? throw new InvalidOperationException("Windows Firewall is unavailable."))!;
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public IReadOnlyList<Rule> Read()
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;
        var result = new List<Rule>();
        var names = Servers.Desired(Mode.Party).Select(r => r.Name).ToHashSet();
        try
        {
            foreach (dynamic r in rules)
            {
                try
                {
                    if (names.Contains((string)r.Name))
                    {
                        int protocol = r.Protocol;
                        object? interfaces = r.Interfaces;
                        string interfaceNames = interfaces is Array array ? string.Join("\n", array.Cast<string>()) : "";
                        result.Add(new Rule((string)r.Name, (string)r.RemoteAddresses, (bool)r.Enabled,
                            (string?)r.Grouping ?? "", (int)r.Direction, (int)r.Action, (int)r.Profiles,
                            (int)r.Protocol, (string?)r.ApplicationName ?? "", (string?)r.ServiceName ?? "",
                            (string)r.LocalAddresses, (string)r.InterfaceTypes, (string?)r.Description ?? "",
                            protocol is 6 or 17 ? (string?)r.LocalPorts ?? "" : "",
                            protocol is 6 or 17 ? (string?)r.RemotePorts ?? "" : "",
                            protocol is 1 or 58 ? (string?)r.IcmpTypesAndCodes ?? "" : "", interfaceNames, (bool)r.EdgeTraversal));
                    }
                }
                finally { Release(r); }
            }
            return result;
        }
        finally { Release(rules); Release(policy); }
    }
    public void Add(Rule rule)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;
        dynamic r = Create("HNetCfg.FWRule");
        try
        {
            r.Name = rule.Name; r.Description = rule.Description; r.Grouping = rule.Group;
            r.Protocol = rule.Protocol; r.Direction = rule.Direction; r.Action = rule.Action;
            r.Profiles = rule.Profiles; r.RemoteAddresses = rule.RemoteAddresses;
            r.LocalAddresses = rule.LocalAddresses; r.InterfaceTypes = rule.InterfaceTypes;
            if (rule.Application.Length > 0) r.ApplicationName = rule.Application;
            if (rule.Service.Length > 0) r.ServiceName = rule.Service;
            if (rule.Protocol is 6 or 17)
            {
                r.LocalPorts = rule.LocalPorts.Length == 0 ? "*" : rule.LocalPorts;
                r.RemotePorts = rule.RemotePorts.Length == 0 ? "*" : rule.RemotePorts;
            }
            if (rule.Protocol is 1 or 58 && rule.IcmpTypes.Length > 0) r.IcmpTypesAndCodes = rule.IcmpTypes;
            if (rule.Interfaces.Length > 0) r.Interfaces = rule.Interfaces.Split('\n');
            r.EdgeTraversal = rule.EdgeTraversal;
            r.Enabled = rule.Enabled;
            rules.Add(r);
            Log.Write("Firewall rule created: " + rule.Name + "; enabled=" + rule.Enabled);
        }
        finally { Release(r); Release(rules); Release(policy); }
    }
    public void Remove(string name)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2"); dynamic rules = policy.Rules;
        try { rules.Remove(name); }
        finally { Release(rules); Release(policy); }
    }
    public bool IsFirewallActive()
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        try
        {
            int profiles = policy.CurrentProfileTypes;
            foreach (var profile in new[] { 1, 2, 4 })
                if ((profiles & profile) != 0 && !(bool)policy.FirewallEnabled[profile]) return false;
            return profiles != 0 && (int)policy.LocalPolicyModifyState == 0;
        }
        finally { Release(policy); }
    }
}

public static class StartupService
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "143OWServerSwitch";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) is string; }
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, "\"" + Environment.ProcessPath + "\" --startup");
        else key.DeleteValue(Name, false);
    }
}

public static class Log
{
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "143OWServerSwitch");
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var folder = Path.Combine(DirectoryPath, "logs"); Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                    File.Move(file, Path.Combine(folder, "app.previous.log"), true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not hide the original error. */ }
    }
}
