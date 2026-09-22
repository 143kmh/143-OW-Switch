using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Excluder.Core;

namespace Excluder.App;

public sealed record EndpointSample(IPAddress Address, string Protocol, long Bytes, int Events, bool TableOnly = false);
public sealed record MatchDiagnostic(EndpointSample? Candidate, string Server, string Provider, string Region, string Location, string Note, DateTimeOffset Detected)
{
    public string CopyText => $"IP: {Candidate?.Address}\nServer: {Server}\nProtocol: {Candidate?.Protocol}\nRegion: {Region}\nProvider: {Provider}\nLocation: {Location}\nDetected: {Detected:O}\n{Note}";
}

public static class ServerDiagnostics
{
    private static TraceEventSession? activeSession;
    public static void Stop()
    {
        try { Interlocked.Exchange(ref activeSession, null)?.Stop(); } catch (Exception error) { Log.Write("Stop network trace: " + error); }
    }
    public static async Task<MatchDiagnostic> Detect(string executable, ServerCatalog catalog, CancellationToken token)
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("Overwatch"))
        {
            using (process)
            {
                var handle = OpenProcess(0x1000, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var path = new StringBuilder(32768); var length = path.Capacity;
                    if (QueryFullProcessImageName(handle, 0, path, ref length) &&
                        path.ToString().Equals(executable, StringComparison.OrdinalIgnoreCase)) pids.Add(process.Id);
                }
                finally { CloseHandle(handle); }
            }
        }
        if (pids.Count == 0) return Empty("Overwatch is not running. Join a match, then detect again.");
        var samples = new Dictionary<string, EndpointSample>();
        void Observe(int pid, IPAddress remote, string protocol, int bytes)
        {
            if (!pids.Contains(pid) || IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) return;
            var key = protocol + ":" + remote;
            samples.TryGetValue(key, out var old);
            samples[key] = new(remote, protocol, (old?.Bytes ?? 0) + Math.Max(0, bytes), (old?.Events ?? 0) + 1);
        }
        string? traceError = null;
        try
        {
            using var session = new TraceEventSession("143OWSwitch.Network." + Guid.NewGuid().ToString("N"));
            activeSession = session;
            session.StopOnDispose = true;
            session.Source.Kernel.UdpIpSend += e => Observe(e.ProcessID, e.daddr, "UDP", e.size);
            session.Source.Kernel.UdpIpRecv += e => Observe(e.ProcessID, e.saddr, "UDP", e.size);
            session.Source.Kernel.UdpIpSendIPV6 += e => Observe(e.ProcessID, e.daddr, "UDP", e.size);
            session.Source.Kernel.UdpIpRecvIPV6 += e => Observe(e.ProcessID, e.saddr, "UDP", e.size);
            session.Source.Kernel.TcpIpSend += e => Observe(e.ProcessID, e.daddr, "TCP", e.size);
            session.Source.Kernel.TcpIpRecv += e => Observe(e.ProcessID, e.saddr, "TCP", e.size);
            session.Source.Kernel.TcpIpSendIPV6 += e => Observe(e.ProcessID, e.daddr, "TCP", e.size);
            session.Source.Kernel.TcpIpRecvIPV6 += e => Observe(e.ProcessID, e.saddr, "TCP", e.size);
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var processing = Task.Run(() => session.Source.Process());
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            finally { Stop(); await processing; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { traceError = error.Message; Log.Write("Network ETW: " + error); }
        token.ThrowIfCancellationRequested();
        if (samples.Count == 0)
            foreach (var sample in ReadTcpTable(pids)) samples.TryAdd("TCP:" + sample.Address, sample);
        var candidate = samples.Values.OrderByDescending(s => s.Protocol == "UDP")
            .ThenByDescending(s => s.Events).ThenByDescending(s => s.Bytes).FirstOrDefault();
        if (candidate == null) return Empty(traceError == null ? "No active remote endpoint found. Detect while a match is running." : "Network tracing unavailable; no TCP candidate found. See log.");
        var definition = catalog.Servers.FirstOrDefault(s => s.Ranges.Any(r => Cidr.Parse(r).Contains(candidate.Address)));
        var cloud = await LookupCloud(candidate.Address, token);
        var note = candidate.Protocol == "UDP" ? "Likely match endpoint · 5-second activity sample; not guaranteed." :
            "TCP candidate only. This may be login/chat; UDP match endpoint was not observed.";
        if (traceError != null) note += " Network tracing unavailable; connection-table fallback.";
        return new(candidate, definition?.Name ?? "Unconfirmed server", cloud.Provider, cloud.Region,
            definition?.Location ?? (cloud.Region == "europe-north1" ? "Finland" : "Region unknown"), note, DateTimeOffset.Now);
    }
    private static MatchDiagnostic Empty(string note) => new(null, "", "", "", "", note, DateTimeOffset.Now);
    private static async Task<(string Provider, string Region)> LookupCloud(IPAddress ip, CancellationToken token)
    {
        var cache = Path.Combine(Log.DirectoryPath, "google-cloud.json"); string? json = null;
        try
        {
            if (File.Exists(cache)) json = await File.ReadAllTextAsync(cache, token);
            if (json == null || File.GetLastWriteTimeUtc(cache) < DateTime.UtcNow.AddDays(-1))
            {
                try
                {
                    var downloaded = Encoding.UTF8.GetString(await RemoteServices.Fetch("https://www.gstatic.com/ipranges/cloud.json", 4 * 1024 * 1024, token));
                    using var check = JsonDocument.Parse(downloaded);
                    if (check.RootElement.GetProperty("prefixes").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid cloud prefixes.");
                    AtomicFile.Write(cache, downloaded); json = downloaded;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { Log.Write("Google ranges: " + error.Message); }
            }
            if (json != null)
            {
                using var document = JsonDocument.Parse(json);
                foreach (var prefix in document.RootElement.GetProperty("prefixes").EnumerateArray())
                {
                    var key = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ipv4Prefix" : "ipv6Prefix";
                    if (prefix.TryGetProperty(key, out var range) && Cidr.Parse(range.GetString()).Contains(ip))
                        return ("Google Cloud", prefix.GetProperty("scope").GetString() ?? "Region unknown");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Log.Write("Region lookup: " + error.Message); }
        return ("Provider unknown", "Region unknown");
    }
    private static IEnumerable<EndpointSample> ReadTcpTable(HashSet<int> pids)
    {
        // OWNER_PID_ALL IPv4 table. UDP owner tables have no remote address, hence the ETW sample above.
        var result = new List<EndpointSample>(); int size = 0;
        uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (error != 122 && error != 0) throw new Win32Exception((int)error);
        for (int retry = 0; retry < 3; retry++)
        {
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(pointer, ref size, false, 2, 5, 0);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception((int)error);
                int count = Marshal.ReadInt32(pointer); int stride = Marshal.SizeOf<TcpRow>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(pointer + 4 + i * stride);
                    if (row.State == 5 && pids.Contains((int)row.Pid)) result.Add(new(new IPAddress(row.RemoteAddress), "TCP", 0, 0, true));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
