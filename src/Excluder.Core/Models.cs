using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excluder.Core;

public enum Mode { Party, Solo }
public static class Servers
{
    public const string RuleGroup = "143OWServerSwitch.Managed.v1";
    public static readonly string[] LegacyNames = ["143 OW Server Switch — GEN1 34.88", "143 OW Server Switch — GEN1 35.228"];
    public static Rule[] Desired(Mode mode, string executable, IEnumerable<ServerDefinition> servers)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFileName(executable).Equals("Overwatch.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Locate Overwatch.exe before creating firewall rules.");
        return servers.SelectMany(server => server.Ranges.Select(range => new Rule(
            $"143 OW Switch — {server.Id} — {range}", Cidr.Parse(range).ToString(), mode == Mode.Solo,
            Application: Path.GetFullPath(executable)))).ToArray();
    }
}

public sealed record Rule(string Name, string RemoteAddresses, bool Enabled,
    string Group = Servers.RuleGroup, int Direction = 2, int Action = 0, int Profiles = int.MaxValue,
    int Protocol = 256, string Application = "", string Service = "", string LocalAddresses = "*",
    string InterfaceTypes = "All", string Description = "Managed by 143 OW Switch.",
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
    public bool Check(Rule[] desired) => Equivalent(store.Read(), desired);
    public int RepairCount(Rule[] desired)
    {
        var actual = store.Read();
        return desired.Count(d => actual.Count(a => a.Name == d.Name) != 1 || !actual.Any(a => Same(a, d))) +
            actual.Count(a => !desired.Any(d => d.Name == a.Name));
    }
    public bool IsActive => store.IsFirewallActive();

    public void Apply(Rule[] desired, Action persist)
    {
        var before = store.Read().ToArray();
        if (desired.Length == 0 || desired.Any(r => string.IsNullOrWhiteSpace(r.Application)))
            throw new InvalidOperationException("Refusing unscoped or empty firewall configuration.");
        if (before.Any(r => r.Group != Servers.RuleGroup && !(r.Group == "" && Servers.LegacyNames.Contains(r.Name))))
            throw new InvalidOperationException("A rule with an app rule name belongs to another group. Rename it in Windows Firewall first.");
        try
        {
            if (!Equivalent(before, desired)) Replace(desired);
            if (!Check(desired)) throw new InvalidOperationException("Windows Firewall did not retain the requested rules.");
            persist();
        }
        catch (Exception original)
        {
            try { Replace(before); }
            catch (Exception rollback) { throw new AggregateException("Update failed and rollback failed. Check Windows Firewall.", original, rollback); }
            throw;
        }
    }

    // Migration must not leave v1 global blocking active while the game path is being located.
    public void DisableUnscoped()
    {
        var before = store.Read().ToArray();
        var unsafeRules = before.Where(r => r.Group == Servers.RuleGroup && r.Application.Length == 0 && r.Enabled).ToArray();
        if (unsafeRules.Length == 0) return;
        foreach (var rule in unsafeRules)
        {
            store.Remove(rule.Name);
            store.Add(rule with { Enabled = false });
        }
        if (store.Read().Any(r => r.Group == Servers.RuleGroup && r.Application.Length == 0 && r.Enabled))
            throw new InvalidOperationException("Could not disable legacy global rules.");
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

    private static bool Same(Rule a, Rule d) =>
        (a with { RemoteAddresses = Cidr.NormalizeFirewall(a.RemoteAddresses), Description = d.Description,
            Application = a.Application.Equals(d.Application, StringComparison.OrdinalIgnoreCase) ? d.Application : a.Application }) == d;
    public static bool Equivalent(IReadOnlyList<Rule> actual, IReadOnlyList<Rule> desired) =>
        actual.Count == desired.Count && desired.All(d => actual.Count(a => Same(a, d)) == 1);
}

public sealed record Settings
{
    public Mode Mode { get; set; } = Mode.Party;
    public bool RunOnStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool Notifications { get; set; } = true;
    public string? OverwatchPath { get; set; }
    public bool AutomaticUpdates { get; set; } = true;
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
