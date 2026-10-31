using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HueStudio.Core.Api;
using HueStudio.Core.Storage;

namespace HueStudio.Core.Discovery;

/// <summary>
/// Pairs this PC with a bridge. The user presses the bridge's link button, then we POST /api with
/// generateclientkey=true. We get back the application key (for CLIP v2) and the client key
/// (the PSK for the Entertainment DTLS stream), and pin the bridge's TLS certificate.
/// </summary>
public static class BridgePairing
{
    public static async Task<BridgeCredentials> PairAsync(
        DiscoveredBridge bridge,
        string deviceName,
        CancellationToken ct = default,
        HttpMessageHandler? handler = null)
    {
        string? seenThumbprint = null;
        using var http = BridgeHttp.Create(
            new Uri($"https://{bridge.Address}/"),
            pinnedSha256: null,
            onCertificateSeen: t => seenThumbprint = t,
            innerHandler: handler);

        var devicetype = $"huestudio#{Sanitise(deviceName)}";
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("api", new { devicetype, generateclientkey = true }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HueApiException($"Couldn't reach the bridge at {bridge.Address}: {ex.Message}", null, ex);
        }

        using (resp)
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var first = (node as JsonArray)?.FirstOrDefault();

            if (first?["error"] is JsonObject err)
            {
                var type = err["type"]?.GetValue<int>();
                if (type == 101) throw new LinkButtonNotPressedException();
                throw new HueApiException(err["description"]?.GetValue<string>() ?? "Pairing failed.", type);
            }

            var success = first?["success"];
            var username = success?["username"]?.GetValue<string>();
            var clientKey = success?["clientkey"]?.GetValue<string>();
            if (string.IsNullOrEmpty(username))
                throw new HueApiException("Unexpected response from the bridge while pairing.");

            return new BridgeCredentials
            {
                BridgeId = bridge.Id,
                Address = bridge.Address,
                Name = bridge.Name,
                ApplicationKey = username,
                ClientKey = clientKey,
                CertificateSha256 = seenThumbprint,
                PairedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// Polls pairing until the link button is pressed or the timeout (default 30s, matching the
    /// bridge's own link window) expires. Lets the UI say "press the button" and just wait.
    /// </summary>
    public static async Task<BridgeCredentials> WaitForLinkButtonAsync(
        DiscoveredBridge bridge,
        string deviceName,
        TimeSpan? timeout = null,
        IProgress<TimeSpan>? remaining = null,
        CancellationToken ct = default,
        HttpMessageHandler? handlerForTests = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromSeconds(1.5);
        while (true)
        {
            try
            {
                return await PairAsync(bridge, deviceName, ct, handlerForTests).ConfigureAwait(false);
            }
            catch (LinkButtonNotPressedException) when (DateTimeOffset.UtcNow + interval < deadline)
            {
                remaining?.Report(deadline - DateTimeOffset.UtcNow);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
    }

    private static string Sanitise(string s)
    {
        var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
        if (clean.Length == 0) clean = "windows";
        return clean.Length > 19 ? clean[..19] : clean;
    }
}
