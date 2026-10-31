using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using HueStudio.Core.Api;
using HueStudio.Core.Models;

namespace HueStudio.Core.State;

/// <summary>
/// Live, in-memory picture of the whole home. Loads a full snapshot, then applies event-stream
/// updates so the UI always reflects reality (including changes made from the phone app or a
/// dimmer switch) without polling.
///
/// Resources are kept as raw JSON and deep-merged on update, then re-materialised into typed
/// models on demand. That way partial events never lose fields we don't model yet.
/// </summary>
public sealed class HomeState
{
    private readonly ConcurrentDictionary<string, JsonObject> _raw = new();
    private readonly ConcurrentDictionary<string, HueResource> _typed = new();

    /// <summary>Raised after a snapshot load or an applied event. Arg is the set of changed resource ids.</summary>
    public event EventHandler<HomeChangedEventArgs>? Changed;

    public DateTimeOffset? LastUpdated { get; private set; }

    public void LoadSnapshot(IEnumerable<JsonObject> resources)
    {
        _raw.Clear();
        _typed.Clear();
        foreach (var r in resources)
            if (r["id"]?.GetValue<string>() is { } id) _raw[id] = (JsonObject)r.DeepClone();
        LastUpdated = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, new HomeChangedEventArgs(_raw.Keys.ToHashSet(), IsSnapshot: true));
    }

    public async Task RefreshAsync(IHueClient client, CancellationToken ct = default) =>
        LoadSnapshot(await client.GetAllResourcesAsync(ct).ConfigureAwait(false));

    public void Apply(HueEvent evt)
    {
        var changed = new HashSet<string>();
        foreach (var item in evt.Data)
        {
            if (item["id"]?.GetValue<string>() is not { } id) continue;
            switch (evt.Type)
            {
                case "add":
                    _raw[id] = (JsonObject)item.DeepClone();
                    break;
                case "update":
                    if (_raw.TryGetValue(id, out var existing))
                        lock (existing) HueJson.Merge(existing, item);
                    else
                        _raw[id] = (JsonObject)item.DeepClone();
                    break;
                case "delete":
                    _raw.TryRemove(id, out _);
                    break;
                default:
                    continue;
            }
            _typed.TryRemove(id, out _);
            changed.Add(id);
        }

        if (changed.Count == 0) return;
        LastUpdated = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, new HomeChangedEventArgs(changed, IsSnapshot: false));
    }

    /// <summary>Optimistic local update so sliders feel instant; the bridge's event confirms it shortly after.</summary>
    public void ApplyLocal(string id, JsonObject patch) =>
        Apply(new HueEvent("update", null, [MergeId(patch, id)]));

    public string? TypeOf(string id) => _raw.TryGetValue(id, out var o) ? o["type"]?.GetValue<string>() : null;

    public T? Get<T>(string id) where T : HueResource
    {
        if (_typed.TryGetValue(id, out var cached)) return cached as T;
        if (!_raw.TryGetValue(id, out var raw)) return null;
        T? typed;
        lock (raw) typed = HueJson.Deserialize<T>(raw);
        if (typed is not null) _typed[id] = typed;
        return typed;
    }

    public IReadOnlyList<T> All<T>(string type) where T : HueResource =>
        _raw.Where(kv => kv.Value["type"]?.GetValue<string>() == type)
            .Select(kv => Get<T>(kv.Key))
            .OfType<T>()
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public IReadOnlyList<Light> Lights => All<Light>(ResourceTypes.Light);
    public IReadOnlyList<Room> Rooms => All<Room>(ResourceTypes.Room);
    public IReadOnlyList<Zone> Zones => All<Zone>(ResourceTypes.Zone);
    public IReadOnlyList<Scene> Scenes => All<Scene>(ResourceTypes.Scene);
    public IReadOnlyList<Device> Devices => All<Device>(ResourceTypes.Device);
    public IReadOnlyList<GroupedLight> GroupedLights => All<GroupedLight>(ResourceTypes.GroupedLight);
    public IReadOnlyList<EntertainmentConfiguration> EntertainmentAreas =>
        All<EntertainmentConfiguration>(ResourceTypes.EntertainmentConfiguration);

    /// <summary>Lights in a room (via its devices) or zone (direct children).</summary>
    public IReadOnlyList<Light> LightsIn(LightGroup group)
    {
        var ids = new List<string>();
        foreach (var child in group.Children)
        {
            if (child.Rtype == ResourceTypes.Light) ids.Add(child.Rid);
            else if (child.Rtype == ResourceTypes.Device && Get<Device>(child.Rid) is { } d) ids.AddRange(d.LightIds);
        }
        return ids.Distinct().Select(Get<Light>).OfType<Light>()
                  .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public GroupedLight? GroupedLightFor(LightGroup group) =>
        group.GroupedLightId is { } id ? Get<GroupedLight>(id) : null;

    public IReadOnlyList<Scene> ScenesFor(LightGroup group) =>
        Scenes.Where(s => s.Group?.Rid == group.Id).ToList();

    /// <summary>The room a light lives in, if any.</summary>
    public Room? RoomOf(Light light) =>
        Rooms.FirstOrDefault(r => r.Children.Any(c => c.Rid == light.Owner?.Rid || c.Rid == light.Id));

    /// <summary>The bridge-wide grouped_light that controls every light in the home.</summary>
    public GroupedLight? WholeHome =>
        GroupedLights.FirstOrDefault(g => g.Owner?.Rtype == ResourceTypes.BridgeHome);

    private static JsonObject MergeId(JsonObject patch, string id)
    {
        var copy = (JsonObject)patch.DeepClone();
        copy["id"] = id;
        return copy;
    }
}

public sealed record HomeChangedEventArgs(IReadOnlySet<string> ChangedIds, bool IsSnapshot);
