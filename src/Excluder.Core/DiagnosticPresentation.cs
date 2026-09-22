using System.Net;

namespace Excluder.Core;

public sealed record EndpointSample(IPAddress Address, string Protocol, long Bytes, int Events, bool TableOnly = false);

public static class EndpointClassification
{
    // Diagnostic-only snapshot of Cloudflare's published proxy ranges, verified 2026-09-23:
    // https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6
    // Never feed this list to firewall rules or infer a city from these anycast addresses.
    private static readonly Cidr[] Cloudflare = new[]
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22", "2400:cb00::/32",
        "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
        "2a06:98c0::/29", "2c0f:f248::/32"
    }.Select(Cidr.Parse).ToArray();

    public static bool IsGeneric(IPAddress address) => Cloudflare.Any(range => range.Contains(address));
    public static bool IsMatchCandidate(EndpointSample sample) => sample.Protocol == "UDP" &&
        sample.Events > 0 && !sample.TableOnly && !IsGeneric(sample.Address);
    public static EndpointSample? Select(IEnumerable<EndpointSample> samples) => samples
        .OrderByDescending(IsMatchCandidate)
        .ThenBy(s => IsGeneric(s.Address))
        .ThenByDescending(s => s.Events).ThenByDescending(s => s.Bytes).FirstOrDefault();
}

public sealed record MatchDiagnostic(EndpointSample? Candidate, string Server, string Provider,
    string Region, string Location, string Note, DateTimeOffset Detected)
{
    public bool IsMatchCandidate => Candidate != null && EndpointClassification.IsMatchCandidate(Candidate);
    public string Heading => Candidate == null ? "" : IsMatchCandidate ? "CURRENT MATCH · CANDIDATE" :
        EffectiveProvider == "Blizzard Entertainment" ? "BLIZZARD ENDPOINT" : "AUXILIARY ENDPOINT";
    private bool Generic => Candidate != null && EndpointClassification.IsGeneric(Candidate.Address);
    private string EffectiveProvider => Generic ? "Cloudflare" : Known(Provider) ? Provider : "Provider unknown";
    private static bool Known(string? value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase);

    public string Details
    {
        get
        {
            if (Candidate == null) return "";
            var lines = new List<string>();
            if (IsMatchCandidate && Known(Server)) lines.Add(Server);
            lines.Add(Candidate.Address.ToString()); lines.Add(EffectiveProvider);
            if (!Generic)
            {
                if (Known(Region)) lines.Add(Region);
                if (Known(Location)) lines.Add(Location);
            }
            return string.Join("\n", lines.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
    public string Explanation => Candidate == null ? Note :
        (IsMatchCandidate ? "Likely UDP match endpoint; candidate, not guaranteed." :
            $"{Candidate.Protocol} connection detected.\nMatch UDP endpoint was not observed." +
            (Generic ? "\nGeneric/CDN infrastructure." : "")) +
        (string.IsNullOrWhiteSpace(Note) ? "" : "\n" + Note);
    public string DisplayText => Candidate == null ? Explanation : $"{Heading}\n\n{Details}\n\n{Explanation}";
    public string CopyText => $"{DisplayText}\n\nProtocol: {Candidate?.Protocol}\nDetected: {Detected:O}";
}
