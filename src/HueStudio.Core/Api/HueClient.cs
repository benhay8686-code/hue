using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using HueStudio.Core.Models;

namespace HueStudio.Core.Api;

/// <summary>Hue API v2 (CLIP v2) client for a paired bridge on the local network.</summary>
public sealed class HueClient : IHueClient
{
    private const string ResourceRoot = "clip/v2/resource";
    private readonly HttpClient _http;
    private readonly HttpClient _events;

    public HueClient(string bridgeAddress, string applicationKey, string? pinnedCertSha256)
        : this(
            BridgeHttp.Create(new Uri($"https://{bridgeAddress}/"), pinnedCertSha256),
            BridgeHttp.Create(new Uri($"https://{bridgeAddress}/"), pinnedCertSha256, timeout: Timeout.InfiniteTimeSpan),
            applicationKey)
    {
    }

    /// <summary>For tests: supply pre-built HttpClients (BaseAddress must be set).</summary>
    public HueClient(HttpClient http, HttpClient events, string applicationKey)
    {
        _http = http;
        _events = events;
        foreach (var c in new[] { _http, _events })
        {
            c.DefaultRequestHeaders.Remove("hue-application-key");
            c.DefaultRequestHeaders.Add("hue-application-key", applicationKey);
        }
    }

    /// <summary>Delay before reconnecting a dropped event stream. Grows to 30s.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<JsonObject>> GetAllResourcesAsync(CancellationToken ct = default)
    {
        var node = await SendAsync(HttpMethod.Get, ResourceRoot, null, ct).ConfigureAwait(false);
        return (node?["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }

    public async Task<IReadOnlyList<T>> GetAsync<T>(string resourceType, CancellationToken ct = default)
    {
        var node = await SendAsync(HttpMethod.Get, $"{ResourceRoot}/{resourceType}", null, ct).ConfigureAwait(false);
        return HueJson.Deserialize<HueResponse<T>>(node)?.Data ?? [];
    }

    public Task UpdateLightAsync(string lightId, LightUpdate update, CancellationToken ct = default) =>
        PutAsync($"{ResourceTypes.Light}/{lightId}", update, ct);

    public Task UpdateGroupedLightAsync(string groupedLightId, GroupedLightUpdate update, CancellationToken ct = default) =>
        PutAsync($"{ResourceTypes.GroupedLight}/{groupedLightId}", update, ct);

    public Task RecallSceneAsync(string sceneId, SceneRecall? recall = null, CancellationToken ct = default) =>
        PutAsync($"{ResourceTypes.Scene}/{sceneId}", new SceneUpdate { Recall = recall ?? new SceneRecall() }, ct);

    public Task UpdateSceneAsync(string sceneId, SceneUpdate update, CancellationToken ct = default) =>
        PutAsync($"{ResourceTypes.Scene}/{sceneId}", update, ct);

    public Task<string> CreateSceneAsync(SceneCreate scene, CancellationToken ct = default) =>
        PostAsync(ResourceTypes.Scene, scene, ct);

    public Task<string> CreateZoneAsync(GroupCreate zone, CancellationToken ct = default) =>
        PostAsync(ResourceTypes.Zone, zone, ct);

    public Task UpdateGroupAsync(string groupType, string groupId, GroupUpdate update, CancellationToken ct = default) =>
        PutAsync($"{groupType}/{groupId}", update, ct);

    public async Task DeleteAsync(string resourceType, string id, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, $"{ResourceRoot}/{resourceType}/{id}", null, ct).ConfigureAwait(false);

    public Task SetEntertainmentStreamingAsync(string configurationId, bool start, CancellationToken ct = default) =>
        PutAsync($"{ResourceTypes.EntertainmentConfiguration}/{configurationId}",
            new EntertainmentConfigurationAction { Action = start ? "start" : "stop" }, ct);

    public async IAsyncEnumerable<HueEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var delay = ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            HttpResponseMessage? response = null;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "eventstream/clip/v2");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                response = await _events.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                delay = ReconnectDelay; // connected: reset backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
            catch (Exception)
            {
                response?.Dispose();
                response = null;
            }

            if (stream is not null)
            {
                await using (stream)
                using (response)
                {
                    var enumerator = EventStreamParser.ParseAsync(stream, ct).GetAsyncEnumerator(ct);
                    while (true)
                    {
                        bool moved;
                        try { moved = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                        catch (Exception) { break; } // dropped connection -> reconnect
                        if (!moved) break;
                        yield return enumerator.Current;
                    }
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
        }
    }

    private async Task PutAsync<T>(string path, T body, CancellationToken ct) =>
        await SendAsync(HttpMethod.Put, $"{ResourceRoot}/{path}", HueJson.Serialize(body), ct).ConfigureAwait(false);

    private async Task<string> PostAsync<T>(string resourceType, T body, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Post, $"{ResourceRoot}/{resourceType}", HueJson.Serialize(body), ct).ConfigureAwait(false);
        return node?["data"]?[0]?["rid"]?.GetValue<string>()
               ?? throw new HueApiException("Bridge did not return the id of the new resource.");
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HueApiException($"Couldn't reach the Hue Bridge: {ex.Message}", null, ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? node = null;
            try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { /* non-JSON error page */ }

            var errors = (node?["errors"] as JsonArray)?
                .Select(e => e?["description"]?.GetValue<string>())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            if (!resp.IsSuccessStatusCode)
            {
                var msg = resp.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                        "The bridge rejected this app's key. Pair again from Settings.",
                    HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
                        "The bridge is busy (rate limited). Try again in a moment.",
                    _ => errors is { Count: > 0 } ? string.Join("; ", errors) : $"Bridge returned {(int)resp.StatusCode}.",
                };
                throw new HueApiException(msg, (int)resp.StatusCode);
            }

            return node;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _events.Dispose();
    }
}
