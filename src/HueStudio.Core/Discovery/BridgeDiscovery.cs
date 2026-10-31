using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using HueStudio.Core.Api;
using HueStudio.Core.Models;

namespace HueStudio.Core.Discovery;

public sealed record DiscoveredBridge(string Id, string Address, int Port = 443)
{
    public string? Name { get; init; }
    public string? ModelId { get; init; }
    public string? SoftwareVersion { get; init; }
}

/// <summary>
/// Finds Hue bridges on the network. Runs mDNS (_hue._tcp.local, works offline) and the Philips
/// discovery endpoint (needs internet) in parallel and merges the results by bridge id.
/// </summary>
public sealed class BridgeDiscovery(HttpClient? http = null)
{
    public static readonly Uri CloudDiscoveryEndpoint = new("https://discovery.meethue.com/");
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<IReadOnlyList<DiscoveredBridge>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var t = timeout ?? TimeSpan.FromSeconds(5);
        var mdns = SafeAsync(() => MdnsDiscovery.QueryAsync(t, ct));
        var cloud = SafeAsync(() => CloudAsync(ct));
        await Task.WhenAll(mdns, cloud).ConfigureAwait(false);

        var merged = new Dictionary<string, DiscoveredBridge>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in mdns.Result.Concat(cloud.Result))
            merged.TryAdd(NormaliseId(b.Id, b.Address), b);

        // Enrich with the bridge's own name / model (unauthenticated endpoint).
        var enriched = await Task.WhenAll(merged.Values.Select(b => EnrichAsync(b, ct))).ConfigureAwait(false);
        return enriched;
    }

    public async Task<IReadOnlyList<DiscoveredBridge>> CloudAsync(CancellationToken ct = default)
    {
        var items = await _http.GetFromJsonAsync<List<CloudItem>>(CloudDiscoveryEndpoint, ct).ConfigureAwait(false);
        return items?.Where(i => !string.IsNullOrEmpty(i.InternalIpAddress))
                   .Select(i => new DiscoveredBridge(i.Id ?? i.InternalIpAddress!, i.InternalIpAddress!, i.Port ?? 443))
                   .ToList() ?? [];
    }

    /// <summary>Checks an address the user typed in and returns bridge info, or null if it isn't a Hue bridge.</summary>
    public static async Task<DiscoveredBridge?> ProbeAsync(string address, CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(address, ct).ConfigureAwait(false);
        return cfg?.Bridgeid is null ? null : new DiscoveredBridge(cfg.Bridgeid, address)
        {
            Name = cfg.Name, ModelId = cfg.Modelid, SoftwareVersion = cfg.Swversion,
        };
    }

    public static async Task<BridgeConfig?> GetConfigAsync(string address, CancellationToken ct = default)
    {
        try
        {
            using var http = BridgeHttp.Create(new Uri($"https://{address}/"), null, timeout: TimeSpan.FromSeconds(4));
            return await http.GetFromJsonAsync<BridgeConfig>("api/0/config", HueJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<DiscoveredBridge> EnrichAsync(DiscoveredBridge b, CancellationToken ct)
    {
        var cfg = await GetConfigAsync(b.Address, ct).ConfigureAwait(false);
        return cfg is null ? b : b with { Name = cfg.Name, ModelId = cfg.Modelid, SoftwareVersion = cfg.Swversion };
    }

    private static string NormaliseId(string id, string address) =>
        string.IsNullOrEmpty(id) ? address : id.Replace(":", "").ToLowerInvariant();

    private static async Task<IReadOnlyList<DiscoveredBridge>> SafeAsync(Func<Task<IReadOnlyList<DiscoveredBridge>>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch { return []; }
    }

    private sealed class CloudItem
    {
        public string? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("internalipaddress")]
        public string? InternalIpAddress { get; set; }
        public int? Port { get; set; }
    }
}

/// <summary>Minimal one-shot mDNS query for _hue._tcp.local (RFC 6762). No dependencies.</summary>
internal static class MdnsDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const string ServiceName = "_hue._tcp.local";

    public static async Task<IReadOnlyList<DiscoveredBridge>> QueryAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var query = BuildQuery(ServiceName);
        await udp.SendAsync(query, MulticastEndpoint, ct).ConfigureAwait(false);

        var results = new Dictionary<string, DiscoveredBridge>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var bridge = ParseResponse(r.Buffer, r.RemoteEndPoint.Address);
                if (bridge is not null) results.TryAdd(bridge.Address, bridge);
            }
        }
        catch (OperationCanceledException) { }
        return results.Values.ToList();
    }

    internal static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        // Header: id=0, flags=0, qdcount=1, an/ns/ar=0
        ms.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        WriteName(ms, name);
        ms.Write([0, 12, 0, 1]); // QTYPE=PTR, QCLASS=IN
        return ms.ToArray();
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            s.WriteByte((byte)bytes.Length);
            s.Write(bytes);
        }
        s.WriteByte(0);
    }

    /// <summary>
    /// Accepts any response that mentions _hue._tcp and uses the A record if present, otherwise
    /// the sender's address. The bridge id comes from the TXT "bridgeid=" entry when available.
    /// </summary>
    internal static DiscoveredBridge? ParseResponse(byte[] packet, IPAddress sender)
    {
        var text = System.Text.Encoding.ASCII.GetString(packet);
        if (!text.Contains("_hue", StringComparison.OrdinalIgnoreCase)) return null;

        string? id = null;
        var idx = text.IndexOf("bridgeid=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + "bridgeid=".Length;
            var end = start;
            while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
            if (end > start) id = text[start..end].ToLowerInvariant();
        }

        var address = TryReadARecord(packet) ?? sender;
        return new DiscoveredBridge(id ?? address.ToString(), address.ToString());
    }

    /// <summary>Scans the packet for an A record (type 1, class IN, rdlength 4).</summary>
    private static IPAddress? TryReadARecord(byte[] p)
    {
        for (var i = 0; i + 14 <= p.Length; i++)
        {
            if (p[i] == 0 && p[i + 1] == 1 && (p[i + 2] & 0x7F) == 0 && p[i + 3] == 1 &&
                p[i + 8] == 0 && p[i + 9] == 4)
            {
                return new IPAddress(p.AsSpan(i + 10, 4));
            }
        }
        return null;
    }
}
