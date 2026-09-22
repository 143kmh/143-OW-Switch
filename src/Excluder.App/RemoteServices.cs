using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Excluder.Core;

namespace Excluder.App;

public static class RemoteServices
{
    public const string Repository = "143kmh/143-OW-Switch";
    public const string CurrentVersion = "1.1.1";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("143OWSwitch/" + CurrentVersion);
        return client;
    }
    public static async Task<byte[]> Fetch(string url, int maxBytes, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMinutes(5)); token = bounded.Token;
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Download exceeds size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var result = new MemoryStream(); var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > maxBytes) throw new InvalidDataException("Download exceeds size limit.");
            await result.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return result.ToArray();
    }
    public static async Task<ServerCatalog?> CheckCatalog(ServerCatalog current, CancellationToken token)
    {
        var bytes = await Fetch($"https://raw.githubusercontent.com/{Repository}/main/servers.json", 128 * 1024, token);
        var candidate = ServerCatalog.Parse(Encoding.UTF8.GetString(bytes));
        return candidate.Date > current.Date ? candidate : null;
    }
    public static async Task<ReleaseInfo?> CheckRelease(CancellationToken token)
    {
        var bytes = await Fetch($"https://api.github.com/repos/{Repository}/releases/latest", 1024 * 1024, token);
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = ReleaseVersion.Parse(root.GetProperty("tag_name").GetString()!);
        if (version.CompareTo(ReleaseVersion.Parse(CurrentVersion)) <= 0) return null;
        string Asset(string name)
        {
            var item = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
            var url = item.GetProperty("browser_download_url").GetString()!;
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("Untrusted update URL.");
            return url;
        }
        return new(version, Asset("143OWSwitch.exe"), Asset("143OWSwitch.exe.sha256"));
    }
}
public sealed record ReleaseInfo(ReleaseVersion Version, string ExecutableUrl, string ChecksumUrl);
