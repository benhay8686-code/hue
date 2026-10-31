using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HueStudio.Core.Api;
using HueStudio.Core.Models;

namespace HueStudio.Core.Demo;

/// <summary>
/// A pretend bridge with a realistic house. Lets the app run with no bridge (demo mode, UI work,
/// screenshots) and backs the unit tests. Behaves like the real thing: updates are merged and
/// echoed back as event-stream "update" events.
/// </summary>
public sealed class DemoHueClient : IHueClient
{
    private readonly Dictionary<string, JsonObject> _res = new();
    private readonly Lock _gate = new();
    private readonly List<Channel<HueEvent>> _subscribers = [];
    private int _seq;

    public DemoHueClient() => Seed();

    public Task<IReadOnlyList<JsonObject>> GetAllResourcesAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<JsonObject>>(_res.Values.Select(v => (JsonObject)v.DeepClone()).ToList());
    }

    public Task<IReadOnlyList<T>> GetAsync<T>(string resourceType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _res.Values.Where(v => v["type"]?.GetValue<string>() == resourceType)
                .Select(v => HueJson.Deserialize<T>(v)!).ToList();
            return Task.FromResult<IReadOnlyList<T>>(list);
        }
    }

    public Task UpdateLightAsync(string lightId, LightUpdate update, CancellationToken ct = default)
    {
        var patch = ToPatch(update);
        patch.Remove("alert");
        if (patch["color"] is not null) patch["color_temperature"] = new JsonObject { ["mirek_valid"] = false, ["mirek"] = null };
        if (patch["color_temperature"]?["mirek"] is not null) patch["color_temperature"]!["mirek_valid"] = true;
        Patch(lightId, patch);
        return Task.CompletedTask;
    }

    public Task UpdateGroupedLightAsync(string groupedLightId, GroupedLightUpdate update, CancellationToken ct = default)
    {
        var patch = ToPatch(update);
        patch.Remove("alert");
        var lights = LightsForGroupedLight(groupedLightId);
        Patch(groupedLightId, new JsonObject { ["on"] = patch["on"]?.DeepClone(), ["dimming"] = patch["dimming"]?.DeepClone() }.WithoutNulls());
        var lu = HueJson.Deserialize<LightUpdate>(patch)!;
        foreach (var l in lights) UpdateLightAsync(l, lu, ct);
        return Task.CompletedTask;
    }

    public Task RecallSceneAsync(string sceneId, SceneRecall? recall = null, CancellationToken ct = default)
    {
        JsonObject scene;
        lock (_gate) scene = (JsonObject)_res[sceneId].DeepClone();
        var group = scene["group"]?["rid"]?.GetValue<string>();
        foreach (var a in (scene["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var target = a["target"]?["rid"]?.GetValue<string>();
            if (target is null) continue;
            var act = HueJson.Deserialize<LightUpdate>(a["action"])!;
            if (recall?.Dimming is { } d) act.Dimming = d;
            UpdateLightAsync(target, act, ct);
        }
        List<string> siblings;
        lock (_gate)
            siblings = _res.Values
                .Where(v => v["type"]?.GetValue<string>() == ResourceTypes.Scene && v["group"]?["rid"]?.GetValue<string>() == group)
                .Select(v => v["id"]!.GetValue<string>()).ToList();
        foreach (var s in siblings)
        {
            var active = s != sceneId ? "inactive" : recall?.Action == "dynamic_palette" ? "dynamic_palette" : "static";
            Patch(s, new JsonObject { ["status"] = new JsonObject { ["active"] = active } });
        }
        return Task.CompletedTask;
    }

    public Task UpdateSceneAsync(string sceneId, SceneUpdate update, CancellationToken ct = default)
    {
        if (update.Recall is not null) return RecallSceneAsync(sceneId, update.Recall, ct);
        Patch(sceneId, ToPatch(update));
        return Task.CompletedTask;
    }

    public Task<string> CreateSceneAsync(SceneCreate scene, CancellationToken ct = default) => Task.FromResult(Add(ToPatch(scene)));

    public Task<string> CreateZoneAsync(GroupCreate zone, CancellationToken ct = default)
    {
        var id = Add(ToPatch(zone));
        var gl = Add(new JsonObject { ["type"] = ResourceTypes.GroupedLight, ["owner"] = Ref(id, ResourceTypes.Zone), ["on"] = On(false), ["dimming"] = Dim(0) });
        Patch(id, new JsonObject { ["services"] = new JsonArray(Ref(gl, ResourceTypes.GroupedLight)) });
        return Task.FromResult(id);
    }

    public Task UpdateGroupAsync(string groupType, string groupId, GroupUpdate update, CancellationToken ct = default)
    {
        Patch(groupId, ToPatch(update));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string resourceType, string id, CancellationToken ct = default)
    {
        lock (_gate) _res.Remove(id);
        Publish(new HueEvent("delete", Now(), [new JsonObject { ["id"] = id, ["type"] = resourceType }]));
        return Task.CompletedTask;
    }

    public Task SetEntertainmentStreamingAsync(string configurationId, bool start, CancellationToken ct = default)
    {
        Patch(configurationId, new JsonObject { ["status"] = start ? "active" : "inactive" });
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<HueEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateUnbounded<HueEvent>();
        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return e;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }

    public void Dispose()
    {
        lock (_gate) foreach (var s in _subscribers) s.Writer.TryComplete();
    }

    // ---- internals -------------------------------------------------------------------------

    private void Patch(string id, JsonObject patch)
    {
        string type;
        lock (_gate)
        {
            if (!_res.TryGetValue(id, out var existing)) throw new HueApiException($"Resource {id} not found.", 404);
            HueJson.Merge(existing, patch);
            type = existing["type"]!.GetValue<string>();
        }
        var data = (JsonObject)patch.DeepClone();
        data["id"] = id;
        data["type"] = type;
        Publish(new HueEvent("update", Now(), [data]));
    }

    private string Add(JsonObject obj)
    {
        var id = NewId();
        obj["id"] = id;
        lock (_gate) _res[id] = obj;
        Publish(new HueEvent("add", Now(), [(JsonObject)obj.DeepClone()]));
        return id;
    }

    private void Publish(HueEvent e)
    {
        lock (_gate) foreach (var s in _subscribers) s.Writer.TryWrite(e);
    }

    private List<string> LightsForGroupedLight(string groupedLightId)
    {
        lock (_gate)
        {
            var owner = _res[groupedLightId]["owner"];
            var ownerId = owner?["rid"]?.GetValue<string>();
            var ownerType = owner?["rtype"]?.GetValue<string>();
            if (ownerType == ResourceTypes.BridgeHome)
                return _res.Values.Where(v => v["type"]?.GetValue<string>() == ResourceTypes.Light).Select(v => v["id"]!.GetValue<string>()).ToList();

            var group = ownerId is null ? null : _res.GetValueOrDefault(ownerId);
            var result = new List<string>();
            foreach (var c in (group?["children"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var rid = c["rid"]!.GetValue<string>();
                if (c["rtype"]?.GetValue<string>() == ResourceTypes.Light) result.Add(rid);
                else if (_res.TryGetValue(rid, out var dev))
                    result.AddRange((dev["services"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(s => s["rtype"]?.GetValue<string>() == ResourceTypes.Light)
                        .Select(s => s["rid"]!.GetValue<string>()));
            }
            return result;
        }
    }

    private static JsonObject ToPatch<T>(T value) => (JsonObject)JsonNode.Parse(HueJson.Serialize(value))!;
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    private string NewId() => $"00000000-0000-4000-8000-{Interlocked.Increment(ref _seq):D12}";
    private static JsonObject Ref(string rid, string rtype) => new() { ["rid"] = rid, ["rtype"] = rtype };
    private static JsonObject On(bool on) => new() { ["on"] = on };
    private static JsonObject Dim(double b) => new() { ["brightness"] = b };
    private static JsonObject Meta(string name, string archetype) => new() { ["name"] = name, ["archetype"] = archetype };

    private void Seed()
    {
        var home = NewId();
        var homeGl = NewId();
        var allRooms = new List<string>();

        string AddLight(string device, string name, string archetype, bool color, bool on, double bri, (double x, double y)? xy = null, int? mirek = null)
        {
            var id = NewId();
            var light = new JsonObject
            {
                ["id"] = id, ["type"] = ResourceTypes.Light, ["owner"] = Ref(device, ResourceTypes.Device),
                ["metadata"] = Meta(name, archetype), ["on"] = On(on),
                ["dimming"] = new JsonObject { ["brightness"] = bri, ["min_dim_level"] = 0.2 },
                ["color_temperature"] = new JsonObject
                {
                    ["mirek"] = mirek, ["mirek_valid"] = mirek is not null,
                    ["mirek_schema"] = new JsonObject { ["mirek_minimum"] = 153, ["mirek_maximum"] = 500 },
                },
                ["mode"] = "normal",
            };
            if (color)
            {
                var p = xy ?? (0.4573, 0.41);
                light["color"] = new JsonObject
                {
                    ["xy"] = new JsonObject { ["x"] = p.x, ["y"] = p.y },
                    ["gamut_type"] = "C",
                    ["gamut"] = new JsonObject
                    {
                        ["red"] = new JsonObject { ["x"] = 0.6915, ["y"] = 0.3083 },
                        ["green"] = new JsonObject { ["x"] = 0.17, ["y"] = 0.7 },
                        ["blue"] = new JsonObject { ["x"] = 0.1532, ["y"] = 0.0475 },
                    },
                };
            }
            _res[id] = light;
            _res[device] = new JsonObject
            {
                ["id"] = device, ["type"] = ResourceTypes.Device, ["metadata"] = Meta(name, archetype),
                ["product_data"] = new JsonObject
                {
                    ["model_id"] = color ? "LCA006" : "LWA017", ["manufacturer_name"] = "Signify Netherlands B.V.",
                    ["product_name"] = color ? "Hue color lamp" : "Hue white lamp", ["software_version"] = "1.122.2",
                },
                ["services"] = new JsonArray(Ref(id, ResourceTypes.Light)),
            };
            return id;
        }

        (string room, List<string> lights) AddRoom(string name, string archetype, params (string name, string arch, bool color, bool on, double bri, (double, double)? xy, int? mirek)[] lights)
        {
            var room = NewId();
            var gl = NewId();
            var lightIds = new List<string>();
            var devices = new JsonArray();
            foreach (var l in lights)
            {
                var dev = NewId();
                lightIds.Add(AddLight(dev, l.name, l.arch, l.color, l.on, l.bri, l.xy, l.mirek));
                devices.Add(Ref(dev, ResourceTypes.Device));
            }
            var anyOn = lights.Any(l => l.on);
            _res[room] = new JsonObject
            {
                ["id"] = room, ["type"] = ResourceTypes.Room, ["metadata"] = Meta(name, archetype),
                ["children"] = devices, ["services"] = new JsonArray(Ref(gl, ResourceTypes.GroupedLight)),
            };
            _res[gl] = new JsonObject
            {
                ["id"] = gl, ["type"] = ResourceTypes.GroupedLight, ["owner"] = Ref(room, ResourceTypes.Room),
                ["on"] = On(anyOn), ["dimming"] = Dim(anyOn ? lights.Where(l => l.on).Average(l => l.bri) : 0),
            };
            allRooms.Add(room);
            return (room, lightIds);
        }

        void AddScene(string group, string groupType, string name, IEnumerable<string> lights, Func<int, JsonObject> action)
        {
            var id = NewId();
            var actions = new JsonArray();
            var i = 0;
            foreach (var l in lights)
                actions.Add(new JsonObject { ["target"] = Ref(l, ResourceTypes.Light), ["action"] = action(i++) });
            _res[id] = new JsonObject
            {
                ["id"] = id, ["type"] = ResourceTypes.Scene, ["metadata"] = new JsonObject { ["name"] = name },
                ["group"] = Ref(group, groupType), ["actions"] = actions, ["speed"] = 0.6, ["auto_dynamic"] = false,
                ["status"] = new JsonObject { ["active"] = "inactive" },
            };
        }

        static JsonObject ColorAction((double x, double y) xy, double bri) => new()
        {
            ["on"] = new JsonObject { ["on"] = true }, ["dimming"] = new JsonObject { ["brightness"] = bri },
            ["color"] = new JsonObject { ["xy"] = new JsonObject { ["x"] = xy.x, ["y"] = xy.y } },
        };
        static JsonObject WhiteAction(int mirek, double bri) => new()
        {
            ["on"] = new JsonObject { ["on"] = true }, ["dimming"] = new JsonObject { ["brightness"] = bri },
            ["color_temperature"] = new JsonObject { ["mirek"] = mirek },
        };

        var living = AddRoom("Living room", "living_room",
            ("Floor lamp", "floor_shade", true, true, 72, (0.5016, 0.4151), null),
            ("Ceiling", "ceiling_round", true, true, 60, null, 366),
            ("TV lightstrip", "hue_lightstrip_tv", true, true, 45, (0.1691, 0.0441), null),
            ("Play bar left", "hue_play", true, false, 0, (0.4, 0.2), null),
            ("Play bar right", "hue_play", true, false, 0, (0.4, 0.2), null));
        var kitchen = AddRoom("Kitchen", "kitchen",
            ("Pendant 1", "pendant_round", false, true, 100, null, 233),
            ("Pendant 2", "pendant_round", false, true, 100, null, 233),
            ("Under-cabinet strip", "hue_lightstrip", true, false, 0, null, 300));
        var bedroom = AddRoom("Bedroom", "bedroom",
            ("Bedside left", "table_shade", true, false, 0, (0.5612, 0.4042), null),
            ("Bedside right", "table_shade", true, false, 0, (0.5612, 0.4042), null),
            ("Ceiling", "ceiling_round", false, false, 0, null, 400));
        var office = AddRoom("Office", "office",
            ("Desk lamp", "desk_lamp", true, true, 85, null, 200),
            ("Hue Play gradient", "hue_lightstrip_pc", true, true, 50, (0.2, 0.1), null));
        var hallway = AddRoom("Hallway", "hallway",
            ("Spot 1", "recessed_ceiling", false, false, 0, null, 366),
            ("Spot 2", "recessed_ceiling", false, false, 0, null, 366));

        // Zone spanning the TV corner and kitchen strip.
        var zone = NewId();
        var zoneGl = NewId();
        var zoneLights = new[] { living.lights[2], living.lights[3], living.lights[4], kitchen.lights[2] };
        _res[zone] = new JsonObject
        {
            ["id"] = zone, ["type"] = ResourceTypes.Zone, ["metadata"] = Meta("Movie corner", "tv"),
            ["children"] = new JsonArray(zoneLights.Select(l => (JsonNode)Ref(l, ResourceTypes.Light)).ToArray()),
            ["services"] = new JsonArray(Ref(zoneGl, ResourceTypes.GroupedLight)),
        };
        _res[zoneGl] = new JsonObject { ["id"] = zoneGl, ["type"] = ResourceTypes.GroupedLight, ["owner"] = Ref(zone, ResourceTypes.Zone), ["on"] = On(true), ["dimming"] = Dim(45) };

        _res[home] = new JsonObject { ["id"] = home, ["type"] = ResourceTypes.BridgeHome, ["children"] = new JsonArray(allRooms.Select(r => (JsonNode)Ref(r, ResourceTypes.Room)).ToArray()), ["services"] = new JsonArray(Ref(homeGl, ResourceTypes.GroupedLight)) };
        _res[homeGl] = new JsonObject { ["id"] = homeGl, ["type"] = ResourceTypes.GroupedLight, ["owner"] = Ref(home, ResourceTypes.BridgeHome), ["on"] = On(true), ["dimming"] = Dim(60) };

        (double, double)[] sunset = [(0.5916, 0.3824), (0.5016, 0.4151), (0.4402, 0.2), (0.6, 0.35), (0.55, 0.41)];
        (double, double)[] arctic = [(0.1691, 0.0441), (0.2, 0.3), (0.17, 0.2), (0.25, 0.25), (0.3, 0.32)];
        foreach (var (room, lights) in new[] { living, kitchen, bedroom, office, hallway })
        {
            AddScene(room, ResourceTypes.Room, "Bright", lights, _ => WhiteAction(233, 100));
            AddScene(room, ResourceTypes.Room, "Relax", lights, _ => WhiteAction(447, 56));
            AddScene(room, ResourceTypes.Room, "Concentrate", lights, _ => WhiteAction(233, 100));
            AddScene(room, ResourceTypes.Room, "Nightlight", lights, _ => ColorAction((0.5612, 0.4042), 1));
            AddScene(room, ResourceTypes.Room, "Savanna sunset", lights, i => ColorAction(sunset[i % sunset.Length], 70));
            AddScene(room, ResourceTypes.Room, "Arctic aurora", lights, i => ColorAction(arctic[i % arctic.Length], 65));
        }
        AddScene(zone, ResourceTypes.Zone, "Cinema", zoneLights, i => ColorAction(arctic[i % arctic.Length], 20));

        // Entertainment area for the TV.
        var ent = NewId();
        _res[ent] = new JsonObject
        {
            ["id"] = ent, ["type"] = ResourceTypes.EntertainmentConfiguration, ["metadata"] = new JsonObject { ["name"] = "TV area" },
            ["configuration_type"] = "screen", ["status"] = "inactive",
            ["channels"] = new JsonArray(zoneLights.Take(3).Select((l, i) => (JsonNode)new JsonObject
            {
                ["channel_id"] = i,
                ["position"] = new JsonObject { ["x"] = -0.8 + 0.8 * i, ["y"] = 0.8, ["z"] = 0 },
                ["members"] = new JsonArray(),
            }).ToArray()),
        };
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject WithoutNulls(this JsonObject o)
    {
        foreach (var k in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) o.Remove(k);
        return o;
    }
}
