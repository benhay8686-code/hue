namespace HueStudio.Core.Models;

/// <summary>Resource type names used by the Hue API v2 (CLIP v2).</summary>
public static class ResourceTypes
{
    public const string Light = "light";
    public const string Room = "room";
    public const string Zone = "zone";
    public const string GroupedLight = "grouped_light";
    public const string Scene = "scene";
    public const string Device = "device";
    public const string BridgeHome = "bridge_home";
    public const string EntertainmentConfiguration = "entertainment_configuration";
    public const string Entertainment = "entertainment";
    public const string Motion = "motion";
    public const string Button = "button";
    public const string BehaviorInstance = "behavior_instance";
    public const string SmartScene = "smart_scene";
}

public abstract class HueResource
{
    public string Id { get; set; } = "";
    public string? IdV1 { get; set; }
    public string Type { get; set; } = "";
    public Metadata? Metadata { get; set; }
    public string Name => Metadata?.Name ?? Id;
}

public sealed class Light : HueResource
{
    public ResourceRef? Owner { get; set; }
    public OnState? On { get; set; }
    public Dimming? Dimming { get; set; }
    public ColorTemperatureState? ColorTemperature { get; set; }
    public ColorState? Color { get; set; }
    public GradientState? Gradient { get; set; }
    public Dynamics? Dynamics { get; set; }
    public EffectsState? Effects { get; set; }
    /// <summary>"normal" or "streaming" (entertainment).</summary>
    public string? Mode { get; set; }

    public bool IsOn => On?.On ?? false;
    public bool SupportsColor => Color is not null;
    public bool SupportsColorTemperature => ColorTemperature is not null;
    public bool SupportsDimming => Dimming is not null;
}

/// <summary>Rooms and zones share the same shape: a set of children and a grouped_light service.</summary>
public abstract class LightGroup : HueResource
{
    public List<ResourceRef> Children { get; set; } = [];
    public List<ResourceRef> Services { get; set; } = [];

    public string? GroupedLightId =>
        Services.FirstOrDefault(s => s.Rtype == ResourceTypes.GroupedLight)?.Rid;
}

/// <summary>A room. Children are devices (a device belongs to exactly one room).</summary>
public sealed class Room : LightGroup;

/// <summary>A zone. Children are lights (a light can be in many zones).</summary>
public sealed class Zone : LightGroup;

public sealed class GroupedLight : HueResource
{
    public ResourceRef? Owner { get; set; }
    public OnState? On { get; set; }
    public Dimming? Dimming { get; set; }
    public bool IsOn => On?.On ?? false;
}

public sealed class SceneLightAction
{
    public OnState? On { get; set; }
    public Dimming? Dimming { get; set; }
    public ColorState? Color { get; set; }
    public ColorTemperatureState? ColorTemperature { get; set; }
    public GradientState? Gradient { get; set; }
    public EffectsState? Effects { get; set; }
}

public sealed class SceneAction
{
    public ResourceRef Target { get; set; } = new("", ResourceTypes.Light);
    public SceneLightAction Action { get; set; } = new();
}

public sealed class ScenePaletteColor
{
    public ColorState Color { get; set; } = new();
    public Dimming? Dimming { get; set; }
}

public sealed class ScenePalette
{
    public List<ScenePaletteColor> Color { get; set; } = [];
    public List<Dimming> Dimming { get; set; } = [];
}

public sealed class SceneStatus
{
    /// <summary>"inactive", "static" or "dynamic_palette".</summary>
    public string? Active { get; set; }
}

public sealed class Scene : HueResource
{
    public List<SceneAction> Actions { get; set; } = [];
    public ScenePalette? Palette { get; set; }
    public ResourceRef? Group { get; set; }
    public double? Speed { get; set; }
    public bool? AutoDynamic { get; set; }
    public SceneStatus? Status { get; set; }

    public bool IsActive => Status?.Active is { } a && a != "inactive";
}

public sealed class Device : HueResource
{
    public ProductData? ProductData { get; set; }
    public List<ResourceRef> Services { get; set; } = [];

    public IEnumerable<string> LightIds =>
        Services.Where(s => s.Rtype == ResourceTypes.Light).Select(s => s.Rid);
}

public sealed class EntertainmentChannel
{
    public int ChannelId { get; set; }
    public EntertainmentPosition? Position { get; set; }
    public List<EntertainmentChannelMember> Members { get; set; } = [];
}

public sealed class EntertainmentChannelMember
{
    public ResourceRef? Service { get; set; }
    public int Index { get; set; }
}

public sealed class EntertainmentPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class EntertainmentConfiguration : HueResource
{
    /// <summary>"screen", "monitor", "music", "3dspace" or "other".</summary>
    public string? ConfigurationType { get; set; }
    /// <summary>"active" or "inactive".</summary>
    public string? Status { get; set; }
    public ResourceRef? ActiveStreamer { get; set; }
    public List<EntertainmentChannel> Channels { get; set; } = [];
    public List<ResourceRef> LightServices { get; set; } = [];
}

/// <summary>Unauthenticated bridge info from GET /api/0/config.</summary>
public sealed class BridgeConfig
{
    public string? Name { get; set; }
    public string? Bridgeid { get; set; }
    public string? Modelid { get; set; }
    public string? Swversion { get; set; }
    public string? Apiversion { get; set; }
    public string? Mac { get; set; }
}
