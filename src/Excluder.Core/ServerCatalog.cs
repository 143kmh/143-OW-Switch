using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Excluder.Core;

public sealed record ServerDefinition(string Id, string Name, string Location, string[] Ranges);
public sealed record ServerCatalog(int SchemaVersion, string Updated, ServerDefinition[] Servers)
{
    public ServerDefinition Finland => Servers.Single(s => s.Id == "gen1");
    public DateOnly Date => DateOnly.ParseExact(Updated, "yyyy-MM-dd");
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static ServerCatalog Parse(string json)
    {
        if (json.Length > 128 * 1024) throw new InvalidDataException("Server configuration is too large.");
        var catalog = JsonSerializer.Deserialize<ServerCatalog>(json, JsonOptions) ?? throw new InvalidDataException("Empty server configuration.");
        if (catalog.SchemaVersion != 1 || !DateOnly.TryParseExact(catalog.Updated, "yyyy-MM-dd", out _) ||
            catalog.Servers is not { Length: > 0 and <= 64 }) throw new InvalidDataException("Unsupported server configuration.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in catalog.Servers)
        {
            if (server == null || server.Id == null || !Regex.IsMatch(server.Id, "^[a-z0-9-]{1,32}$") || !ids.Add(server.Id) ||
                string.IsNullOrWhiteSpace(server.Name) || server.Name.Length > 64 || string.IsNullOrWhiteSpace(server.Location) ||
                server.Ranges is not { Length: > 0 and <= 256 }) throw new InvalidDataException("Invalid server definition.");
            var ranges = new HashSet<string>();
            foreach (var range in server.Ranges)
                if (!ranges.Add(Cidr.Parse(range).ToString())) throw new InvalidDataException("Duplicate CIDR range.");
        }
        if (!catalog.Servers.Any(s => s.Id == "gen1")) throw new InvalidDataException("GEN1 definition is required.");
        return catalog;
    }
    public static ServerCatalog Bundled()
    {
        using var stream = typeof(ServerCatalog).Assembly.GetManifestResourceStream("Excluder.Core.servers.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

public sealed class CatalogStore(string directory)
{
    private string CachePath => Path.Combine(directory, "servers.json");
    public ServerCatalog Load(Action<Exception> log)
    {
        if (File.Exists(CachePath))
        {
            try { return ServerCatalog.Parse(File.ReadAllText(CachePath)); }
            catch (Exception error) { log(error); }
        }
        return ServerCatalog.Bundled();
    }
    public void Save(ServerCatalog catalog) => AtomicFile.Write(CachePath, JsonSerializer.Serialize(catalog, ServerCatalog.JsonOptions));
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class Cidr
{
    public IPAddress Network { get; }
    public int Prefix { get; }
    private Cidr(IPAddress network, int prefix) { Network = network; Prefix = prefix; }
    public static Cidr Parse(string? text)
    {
        var parts = text?.Split('/');
        if (parts?.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.ScopeIdSafe() != 0 ||
            !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > ip.GetAddressBytes().Length * 8)
            throw new InvalidDataException("Invalid CIDR: " + text);
        var bytes = ip.GetAddressBytes();
        for (int bit = prefix; bit < bytes.Length * 8; bit++)
            if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != 0) throw new InvalidDataException("CIDR must use a network address: " + text);
        return new Cidr(ip, prefix);
    }
    public bool Contains(IPAddress ip)
    {
        var left = Network.GetAddressBytes(); var right = ip.GetAddressBytes();
        if (left.Length != right.Length) return false;
        for (int bit = 0; bit < Prefix; bit++)
            if (((left[bit / 8] ^ right[bit / 8]) & (1 << (7 - bit % 8))) != 0) return false;
        return true;
    }
    public override string ToString() => Network + "/" + Prefix;
    public static string NormalizeFirewall(string address)
    {
        try
        {
            if (address.Contains('-'))
            {
                var ends = address.Split('-'); var first = IPAddress.Parse(ends[0]); var last = IPAddress.Parse(ends[1]);
                var a = first.GetAddressBytes(); var b = last.GetAddressBytes();
                if (a.Length != b.Length) return address;
                int prefix = 0;
                while (prefix < a.Length * 8 && ((a[prefix / 8] ^ b[prefix / 8]) & (1 << (7 - prefix % 8))) == 0) prefix++;
                for (int bit = prefix; bit < a.Length * 8; bit++)
                    if ((a[bit / 8] & (1 << (7 - bit % 8))) != 0 || (b[bit / 8] & (1 << (7 - bit % 8))) == 0) return address;
                return Parse(first + "/" + prefix).ToString();
            }
            var parts = address.Split('/');
            if (parts.Length == 2 && parts[1].Contains('.'))
            {
                var mask = IPAddress.Parse(parts[1]).GetAddressBytes(); int count = 0; bool zero = false;
                foreach (var octet in mask) for (int bit = 7; bit >= 0; bit--)
                { bool on = (octet & (1 << bit)) != 0; if (on && zero) return address; if (on) count++; else zero = true; }
                return Parse(parts[0] + "/" + count).ToString();
            }
            return Parse(address).ToString();
        }
        catch { return address; }
    }
}
internal static class AddressExtensions
{
    public static long ScopeIdSafe(this IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
}
