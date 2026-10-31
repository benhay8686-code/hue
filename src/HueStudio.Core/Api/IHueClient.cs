using System.Text.Json.Nodes;
using HueStudio.Core.Models;

namespace HueStudio.Core.Api;

/// <summary>
/// Everything the app needs from a bridge. Implemented by <see cref="HueClient"/> (real bridge,
/// Hue API v2 / CLIP v2) and <see cref="Demo.DemoHueClient"/> (in-memory house for demo mode and tests).
/// </summary>
public interface IHueClient : IDisposable
{
    /// <summary>Raw snapshot of every resource on the bridge (GET /clip/v2/resource).</summary>
    Task<IReadOnlyList<JsonObject>> GetAllResourcesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<T>> GetAsync<T>(string resourceType, CancellationToken ct = default);

    Task UpdateLightAsync(string lightId, LightUpdate update, CancellationToken ct = default);
    Task UpdateGroupedLightAsync(string groupedLightId, GroupedLightUpdate update, CancellationToken ct = default);

    Task RecallSceneAsync(string sceneId, SceneRecall? recall = null, CancellationToken ct = default);
    Task UpdateSceneAsync(string sceneId, SceneUpdate update, CancellationToken ct = default);
    Task<string> CreateSceneAsync(SceneCreate scene, CancellationToken ct = default);

    Task<string> CreateZoneAsync(GroupCreate zone, CancellationToken ct = default);
    Task UpdateGroupAsync(string groupType, string groupId, GroupUpdate update, CancellationToken ct = default);

    Task DeleteAsync(string resourceType, string id, CancellationToken ct = default);

    Task SetEntertainmentStreamingAsync(string configurationId, bool start, CancellationToken ct = default);

    /// <summary>Live change feed (SSE). Reconnects automatically until cancelled.</summary>
    IAsyncEnumerable<HueEvent> EventsAsync(CancellationToken ct = default);
}
