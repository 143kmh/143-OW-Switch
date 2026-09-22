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
        f.Rules.Add(TestRules.Desired(Mode.Party)[0] with { RemoteAddresses = "1.1.1.1", Direction = 1 });
        f.Rules.Add(f.Rules[0]); c.Apply(Mode.Solo, () => { }); Assert(c.Check(Mode.Solo));
        f.Rules.RemoveAt(0); c.Apply(Mode.Party, () => { }); Assert(c.Check(Mode.Party));
    }),
    ("Windows subnet normalization is accepted", () => {
        var actual = TestRules.Desired(Mode.Solo);
        actual[0] = actual[0] with { RemoteAddresses = "34.88.0.0/255.255.0.0" };
        actual[1] = actual[1] with { RemoteAddresses = "35.228.0.0/16" };
        Assert(FirewallController.Equivalent(actual, TestRules.Desired(Mode.Solo)));
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
        var f = new Fake(); f.Rules.Add(TestRules.Desired(Mode.Party)[0] with { Group = "Other app" });
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
    }),
    ("Every rule is executable scoped; missing path is rejected", () => {
        Assert(TestRules.Desired(Mode.Solo).All(r => r.Application.EndsWith("Overwatch.exe")));
        ExpectFailure(() => Servers.Desired(Mode.Solo, "", [ServerCatalog.Bundled().Finland]));
        ExpectFailure(() => Servers.Desired(Mode.Solo, @"C:\Game\Other.exe", [ServerCatalog.Bundled().Finland]));
        var f = new Fake(); var c = new FirewallController(f);
        ExpectFailure(() => c.Apply([new Rule("bad", "1.1.1.0/24", true)], () => { })); Assert(f.Writes == 0);
    }),
    ("Legacy global rules disabled before game discovery", () => {
        var f = new Fake(); f.Rules.Add(new(Servers.LegacyNames[0], "34.88.0.0/16", true));
        var c = new FirewallController(f); c.DisableUnscoped(); Assert(!f.Rules[0].Enabled);
        c.Apply(Mode.Solo, () => { }); Assert(c.Check(Mode.Solo) && f.Rules.All(r => r.Application.Length > 0));
    }),
    ("Changing game path resynchronizes all rules", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Solo, () => { });
        var desired = Servers.Desired(Mode.Solo, @"D:\Moved\Overwatch.exe", [ServerCatalog.Bundled().Finland]);
        Assert(!c.Check(desired)); c.Apply(desired, () => { }); Assert(c.Check(desired));
    }),
    ("Repair count reports one incorrect rule once", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Solo, () => { });
        f.Rules[0] = f.Rules[0] with { Application = @"C:\Wrong\Overwatch.exe" };
        Assert(c.RepairCount(TestRules.Desired(Mode.Solo)) == 1);
    }),
    ("Server data changes add and retire rules without manager changes", () => {
        var f = new Fake(); var c = new FirewallController(f); c.Apply(Mode.Solo, () => { });
        var server = new ServerDefinition("gen1", "GEN1", "Finland", ["192.0.2.0/24"]);
        var desired = Servers.Desired(Mode.Solo, TestRules.Game, [server]);
        c.Apply(desired, () => { }); Assert(f.Rules.Count == 1 && c.Check(desired));
    }),
    ("Malformed schema, duplicate IDs, empty and invalid ranges rejected", () => {
        var original = System.Text.Json.JsonSerializer.Serialize(ServerCatalog.Bundled(), ServerCatalog.JsonOptions);
        foreach (var bad in new[] { "{", "null", "{}", original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
            original.Replace("34.88.0.0/16", "34.88.1.1/16"), original.Replace("35.228.0.0/16", ""),
            original.Replace("35.228.0.0/16", "34.88.0.0/16") }) ExpectFailure(() => ServerCatalog.Parse(bad));
        var duplicate = ServerCatalog.Bundled() with { Servers = [ServerCatalog.Bundled().Finland, ServerCatalog.Bundled().Finland] };
        ExpectFailure(() => ServerCatalog.Parse(System.Text.Json.JsonSerializer.Serialize(duplicate, ServerCatalog.JsonOptions)));
        var empty = ServerCatalog.Bundled() with { Servers = [ServerCatalog.Bundled().Finland with { Ranges = [] }] };
        ExpectFailure(() => ServerCatalog.Parse(System.Text.Json.JsonSerializer.Serialize(empty, ServerCatalog.JsonOptions)));
    }),
    ("CIDR containment works for IPv4 and IPv6", () => {
        Assert(Cidr.Parse("35.228.0.0/16").Contains(System.Net.IPAddress.Parse("35.228.47.196")));
        Assert(!Cidr.Parse("35.228.0.0/16").Contains(System.Net.IPAddress.Parse("35.229.1.1")));
        Assert(Cidr.Parse("2001:db8::/32").Contains(System.Net.IPAddress.Parse("2001:db8::42")));
        Assert(Cidr.NormalizeFirewall("34.88.0.0-34.88.255.255") == "34.88.0.0/16");
        Assert(Cidr.NormalizeFirewall("35.228.0.0/255.255.0.0") == "35.228.0.0/16");
    }),
    ("Offline cache and invalid cache fallback", () => {
        var dir = Path.Combine(Path.GetTempPath(), "143-catalog-test-" + Guid.NewGuid());
        try {
            var cache = new CatalogStore(dir); var bundled = cache.Load(_ => { }); Assert(bundled.Finland.Ranges.Length == 2);
            var newer = bundled with { Updated = "2026-09-23" }; cache.Save(newer); Assert(cache.Load(_ => { }).Date > bundled.Date);
            File.WriteAllText(Path.Combine(dir, "servers.json"), "broken"); bool logged = false;
            Assert(cache.Load(_ => logged = true).Date == bundled.Date && logged);
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }),
    ("Stable semantic version ordering", () => {
        Assert(ReleaseVersion.Parse("v1.10.0").CompareTo(ReleaseVersion.Parse("v1.9.9")) > 0);
        Assert(ReleaseVersion.Parse("1.1.0+build").CompareTo(ReleaseVersion.Parse("v1.1.0")) == 0);
        ExpectFailure(() => ReleaseVersion.Parse("v1.1.0-beta")); ExpectFailure(() => ReleaseVersion.Parse("v1.01.0"));
    }),
    ("Executable swap succeeds and preserves backup until acknowledgement", () => {
        var dir = Path.Combine(Path.GetTempPath(), "143-swap-test-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try {
            var old = Path.Combine(dir, "app.exe"); var next = Path.Combine(dir, "next.exe"); File.WriteAllText(old, "old"); File.WriteAllText(next, "new");
            var backup = ExecutableSwap.Install(next, old, Integrity.Hash(old), Integrity.Hash(next), Guid.NewGuid().ToString("N"), () => {
                Assert(File.ReadAllText(old) == "new"); return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            Assert(File.ReadAllText(old) == "new" && File.ReadAllText(backup) == "old");
        } finally { Directory.Delete(dir, true); }
    }),
    ("Failed update startup restores the previous executable", () => {
        var dir = Path.Combine(Path.GetTempPath(), "143-rollback-test-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try {
            var old = Path.Combine(dir, "app.exe"); var next = Path.Combine(dir, "next.exe"); File.WriteAllText(old, "old"); File.WriteAllText(next, "new");
            ExpectFailure(() => ExecutableSwap.Install(next, old, Integrity.Hash(old), Integrity.Hash(next), Guid.NewGuid().ToString("N"),
                () => throw new IOException("Startup failed")).GetAwaiter().GetResult());
            Assert(File.ReadAllText(old) == "old" && Directory.GetFiles(dir).Length == 2);
            ExpectFailure(() => ExecutableSwap.Install(next, old, Integrity.Hash(old), new string('0',64), Guid.NewGuid().ToString("N"),
                () => Task.CompletedTask).GetAwaiter().GetResult()); Assert(File.ReadAllText(old) == "old");
        } finally { Directory.Delete(dir, true); }
    }),
    ("Checksum validation rejects mismatch and malformed checksum", () => {
        var file = Path.Combine(Path.GetTempPath(), "143-hash-test-" + Guid.NewGuid());
        try {
            File.WriteAllText(file, "test update"); var hash = Integrity.Hash(file);
            Integrity.Verify(file, Integrity.ParseChecksum(hash + "  143OWSwitch.exe"));
            ExpectFailure(() => Integrity.Verify(file, new string('0',64)));
            ExpectFailure(() => Integrity.ParseChecksum(hash + "  another.exe"));
            ExpectFailure(() => Integrity.ParseChecksum("invalid"));
        } finally { File.Delete(file); }
    })
};
var failed = 0;
var allTests = tests.Concat(DiagnosticTests.Cases).ToArray();
foreach (var test in allTests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{allTests.Length - failed}/{allTests.Length} passed");
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

static class TestRules
{
    public const string Game = @"C:\Games\Overwatch\_retail_\Overwatch.exe";
    public static Rule[] Desired(Mode mode) => Servers.Desired(mode, Game, [ServerCatalog.Bundled().Finland]);
    public static void Apply(this FirewallController controller, Mode mode, Action persist) => controller.Apply(Desired(mode), persist);
    public static bool Check(this FirewallController controller, Mode mode) => controller.Check(Desired(mode));
}

