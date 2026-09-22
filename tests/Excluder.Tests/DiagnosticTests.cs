using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Excluder.Core;

static class DiagnosticTests
{
    private static EndpointSample Sample(string ip, string protocol, int events = 1, bool table = false) =>
        new(IPAddress.Parse(ip), protocol, events * 100, events, table);
    private static MatchDiagnostic Result(EndpointSample sample) =>
        new(sample, "Unconfirmed server", "Provider unknown", "Region unknown", "Region unknown", "", DateTimeOffset.Now);
    private static void Assert(bool value) { if (!value) throw new Exception("Diagnostic assertion failed."); }
    public static readonly (string Name, Action Run)[] Cases =
    [
        ("Observed UDP remains an explicitly unconfirmed match candidate", () => {
            var result = Result(Sample("35.228.47.196", "UDP")) with { Server = "GEN1", Provider = "Google Cloud", Region = "europe-north1", Location = "Finland" };
            Assert(result.Heading == "CURRENT MATCH · CANDIDATE" && result.Explanation.Contains("not guaranteed"));
            Assert(result.Details.Contains("GEN1") && result.Details.Contains("europe-north1"));
        }),
        ("TCP table fallback never claims a current match", () => {
            var result = Result(Sample("66.40.191.90", "TCP", 0, true));
            Assert(result.Heading == "AUXILIARY ENDPOINT" && !result.DisplayText.Contains("CURRENT MATCH"));
            Assert(result.Explanation == "TCP connection detected.\nMatch UDP endpoint was not observed.");
            Assert(!result.DisplayText.Contains("Blizzard") && !result.DisplayText.Contains("Amsterdam"));
        }),
        ("Observed ETW TCP is also auxiliary", () => {
            var result = Result(Sample("66.40.191.90", "TCP", 1000));
            Assert(!result.IsMatchCandidate && result.Heading == "AUXILIARY ENDPOINT");
        }),
        ("Cloudflare IPv4 and IPv6 are never match candidates", () => {
            foreach (var ip in new[] { "172.64.152.82", "104.16.1.1", "2606:4700::1111" })
            foreach (var protocol in new[] { "UDP", "TCP" }) {
                var result = Result(Sample(ip, protocol, 100)) with { Location = "Amsterdam", Region = "Region unknown" };
                Assert(result.Heading == "AUXILIARY ENDPOINT" && result.Details.Contains("Cloudflare"));
                Assert(!result.DisplayText.Contains("CURRENT MATCH") && !result.Details.Contains("Amsterdam"));
            }
            Assert(!EndpointClassification.IsGeneric(IPAddress.Parse("172.72.1.1")));
        }),
        ("Selection prefers observed non-CDN UDP over busy generic infrastructure", () => {
            var game = Sample("35.228.47.196", "UDP", 5);
            Assert(EndpointClassification.Select([Sample("172.64.152.82", "UDP", 1000), Sample("66.40.191.90", "TCP", 10000), game]) == game);
            Assert(EndpointClassification.Select([]) == null);
        }),
        ("UDP with no observations or a table-only source is not promoted", () => {
            Assert(!Result(Sample("35.228.47.196", "UDP", 0)).IsMatchCandidate);
            Assert(!Result(Sample("35.228.47.196", "UDP", 1, true)).IsMatchCandidate);
        }),
        ("Unknown metadata is concise and known location is not duplicated", () => {
            var result = Result(Sample("66.40.191.90", "TCP"));
            Assert(result.Details == "66.40.191.90\nProvider unknown");
            var known = result with { Provider = "Blizzard Entertainment", Region = "Amsterdam, Netherlands", Location = "Amsterdam, Netherlands" };
            Assert(known.Heading == "BLIZZARD ENDPOINT" && !known.IsMatchCandidate);
            Assert(known.Details.Split('\n').Count(s => s == "Amsterdam, Netherlands") == 1);
        }),
        ("Copied report preserves auxiliary classification and missing UDP warning", () => {
            var result = Result(Sample("172.64.152.82", "TCP", 0, true));
            Assert(result.CopyText.Contains("AUXILIARY ENDPOINT") && result.CopyText.Contains("Match UDP endpoint was not observed."));
            Assert(result.CopyText.Contains("Protocol: TCP") && result.CopyText.Contains("Detected:") && !result.CopyText.Contains("CURRENT MATCH"));
        }),
        ("No endpoint shows only its actionable message", () => {
            var empty = new MatchDiagnostic(null, "", "", "", "", "Join a match, then detect again.", DateTimeOffset.Now);
            Assert(empty.DisplayText == empty.Note && empty.Heading == "" && empty.Details == "");
        }),
        ("App, updater, visible versions and release notes stay consistent", () => {
            string Read(string name) { using var stream = typeof(DiagnosticTests).Assembly.GetManifestResourceStream("Version." + name)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
            var version = XDocument.Parse(Read("Project")).Descendants("Version").Single().Value;
            Assert(ReleaseVersion.Parse(version).ToString() == version);
            var remote = Read("Remote");
            Assert(Regex.Match(remote, "CurrentVersion = \"([^\"]+)\"").Groups[1].Value == version);
            Assert(remote.Contains("Repository = \"143kmh/143-OW-Switch\""));
            var labels = XDocument.Parse(Read("Window")).Descendants().Attributes("Text").Select(a => a.Value).Where(s => s.StartsWith("Version ")).ToArray();
            Assert(labels.Length == 2 && labels.All(s => s == "Version " + version || s.StartsWith("Version " + version + " ·")));
            Assert(Read("Notes").StartsWith("143 OW Switch " + version));
        })
    ];
}
