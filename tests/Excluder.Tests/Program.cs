using Excluder.Core;

var tests = new (string Name, Action Run)[]
{
    ("First launch creates two disabled rules", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Party, () => { });
        Assert(f.Rules.Count == 2 && f.Rules.All(r => !r.Enabled) && c.Check(Mode.Party));
    }),
    ("SOLO / PARTY transitions and idempotent repair", () => {
        var f = new Fake(); var c = new FirewallController(f);
        c.Apply(Mode.Solo, () => { }); Assert(c.Check(Mode.Solo));
        var mutations = f.Writes; c.Apply(Mode.Solo, () => { }); Assert(f.Writes == mutations);
        c.Apply(Mode.Party, () => { }); Assert(c.Check(Mode.Party));
    }),
    ("Deleted, duplicate and corrupt rules are repaired", () => {
        var f = new Fake(); var c = new FirewallController(f);
        f.Rules.Add(Servers.Desired(Mode.Party)[0] with { RemoteAddresses = "1.1.1.1", Direction = 1 });
        f.Rules.Add(f.Rules[0]); c.Apply(Mode.Solo, () => { }); Assert(c.Check(Mode.Solo));
        f.Rules.RemoveAt(0); c.Apply(Mode.Party, () => { }); Assert(c.Check(Mode.Party));
    }),
    ("Windows subnet normalization is accepted", () => {
        var actual = Servers.Desired(Mode.Solo);
        actual[0] = actual[0] with { RemoteAddresses = "34.88.0.0/255.255.0.0" };
        actual[1] = actual[1] with { RemoteAddresses = "35.228.0.0/16" };
        Assert(FirewallController.Equivalent(actual, Servers.Desired(Mode.Solo)));
    }),
    ("Failed second write restores both original rules", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Party, () => { });
        f.FailAddAt = f.Adds + 2;
        ExpectFailure(() => c.Apply(Mode.Solo, () => throw new Exception("Must not persist")));
        Assert(c.Check(Mode.Party));
    }),
    ("Config failure rolls firewall back", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Party, () => { });
        ExpectFailure(() => c.Apply(Mode.Solo, () => throw new IOException("Disk full")));
        Assert(c.Check(Mode.Party));
    }),
    ("Foreign group name collision is never changed", () => {
        var f = new Fake(); f.Rules.Add(Servers.Desired(Mode.Party)[0] with { Group = "Other app" });
        var c = new FirewallController(f); ExpectFailure(() => c.Apply(Mode.Solo, () => { }));
        ExpectFailure(c.RemoveOwned); Assert(f.Writes == 0);
    }),
    ("Cleanup removes managed rules", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Solo, () => { });
        c.RemoveOwned(); Assert(f.Rules.Count == 0);
    }),
    ("Atomic settings roundtrip, defaults and invalid mode", () => {
        var dir = Path.Combine(Path.GetTempPath(), "143-test-" + Guid.NewGuid());
        try {
            var s = new SettingsStore(dir); Assert(s.Load().Mode == Mode.Party);
            var expected = new Settings { Mode = Mode.Solo, StartMinimized = true, RunOnStartup = true, WindowLeft = -200, WindowTop = 80 };
            s.Save(expected); Assert(s.Load() == expected);
            s.Save(expected with { Mode = Mode.Party }); Assert(s.Load().Mode == Mode.Party);
            Assert(Directory.GetFiles(dir).Length == 1);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"mode\":42}"); ExpectFailure(() => s.Load());
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }),
    ("Manual state drift is detected", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Solo, () => { });
        f.Rules[0] = f.Rules[0] with { Enabled = false }; Assert(!c.Check(Mode.Solo));
    })
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;
static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
static void ExpectFailure(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }

sealed class Fake : IFirewallStore
{
    public List<Rule> Rules { get; } = [];
    public int Writes, Adds, FailAddAt = -1;
    public IReadOnlyList<Rule> Read() => Rules.ToArray();
    public void Remove(string name) { Writes++; var index = Rules.FindIndex(r => r.Name == name); if (index >= 0) Rules.RemoveAt(index); }
    public void Add(Rule rule) { Writes++; Adds++; if (Adds == FailAddAt) throw new IOException("Injected COM failure"); Rules.Add(rule); }
    public bool IsFirewallActive() => true;
}
