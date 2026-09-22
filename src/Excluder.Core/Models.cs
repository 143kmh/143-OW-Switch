using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excluder.Core;

public enum Mode { Party, Solo }
public sealed record ServerGroup(string Id, string Name, string Description, bool Enabled, string[] Ranges);
public static class Servers
{
    public const string RuleGroup = "143OWServerSwitch.Managed.v1";
    public static readonly ServerGroup Gen1 = new("GEN1", "Finland", "Finland server excluded", true,
        ["34.88.0.0-34.88.255.255", "35.228.0.0-35.228.255.255"]);
    public static Rule[] Desired(Mode mode) => Gen1.Ranges.Select((range, index) => new Rule(
        "143 OW Server Switch — GEN1 " + (index == 0 ? "34.88" : "35.228"), range, mode == Mode.Solo)).ToArray();
}

public sealed record Rule(string Name, string RemoteAddresses, bool Enabled,
    string Group = Servers.RuleGroup, int Direction = 2, int Action = 0, int Profiles = int.MaxValue,
    int Protocol = 256, string Application = "", string Service = "", string LocalAddresses = "*",
    string InterfaceTypes = "All", string Description = "Managed by 143 Overwatch Finlad Server Excluder.",
    string LocalPorts = "", string RemotePorts = "", string IcmpTypes = "", string Interfaces = "", bool EdgeTraversal = false);

public interface IFirewallStore
{
    IReadOnlyList<Rule> Read();
    void Remove(string name);
    void Add(Rule rule);
    bool IsFirewallActive();
}

public sealed class FirewallController(IFirewallStore store)
{
    public bool Check(Mode mode) => Equivalent(store.Read(), Servers.Desired(mode));
    public bool IsActive => store.IsFirewallActive();

    public void Apply(Mode mode, Action persist)
    {
        var before = store.Read().ToArray();
        var desired = Servers.Desired(mode);
        if (before.Any(r => r.Group != Servers.RuleGroup && r.Group != ""))
            throw new InvalidOperationException("A rule with an app rule name belongs to another group. Rename it in Windows Firewall first.");
        try
        {
            if (!Equivalent(before, desired)) Replace(desired);
            if (!Check(mode)) throw new InvalidOperationException("Windows Firewall did not retain the requested rules.");
            persist();
        }
        catch (Exception original)
        {
            try { Replace(before); }
            catch (Exception rollback) { throw new AggregateException("Update failed and rollback failed. Check Windows Firewall.", original, rollback); }
            throw;
        }
    }

    public void RemoveOwned()
    {
        var rules = store.Read();
        if (rules.Any(r => r.Group != Servers.RuleGroup))
            throw new InvalidOperationException("An unowned rule has the same name; cleanup was cancelled.");
        foreach (var name in rules.Select(r => r.Name).Distinct())
            foreach (var _ in rules.Where(r => r.Name == name)) store.Remove(name);
        if (store.Read().Count != 0) throw new InvalidOperationException("Some rules could not be removed.");
    }

    private void Replace(IEnumerable<Rule> rules)
    {
        foreach (var rule in store.Read()) store.Remove(rule.Name);
        foreach (var rule in rules) store.Add(rule);
    }

    private static string Normalize(string address) => address
        .Replace("34.88.0.0/255.255.0.0", Servers.Gen1.Ranges[0]).Replace("34.88.0.0/16", Servers.Gen1.Ranges[0])
        .Replace("35.228.0.0/255.255.0.0", Servers.Gen1.Ranges[1]).Replace("35.228.0.0/16", Servers.Gen1.Ranges[1]);

    public static bool Equivalent(IReadOnlyList<Rule> actual, IReadOnlyList<Rule> desired) =>
        actual.Count == desired.Count && desired.All(d => actual.Count(a =>
            (a with { RemoteAddresses = Normalize(a.RemoteAddresses), Description = d.Description }) == d) == 1);
}

public sealed record Settings
{
    public Mode Mode { get; set; } = Mode.Party;
    public bool RunOnStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool Notifications { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath => directory;
    private string PathName => Path.Combine(directory, "config.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public Settings Load()
    {
        if (!File.Exists(PathName)) return new();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName), Options)
            ?? throw new InvalidDataException("Config is empty.");
        if (!Enum.IsDefined(settings.Mode)) throw new InvalidDataException("Unknown saved mode.");
        return settings;
    }
    public void Save(Settings settings)
    {
        Directory.CreateDirectory(directory);
        var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(true);
            }
            File.Move(temp, PathName, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
