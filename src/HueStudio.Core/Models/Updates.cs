namespace HueStudio.Core.Models;

/// <summary>Partial PUT body for a light. Null properties are not sent.</summary>
public sealed class LightUpdate
{
    public OnState? On { get; set; }
    public DimmingUpdate? Dimming { get; set; }
    public ColorUpdate? Color { get; set; }
    public ColorTemperatureUpdate? ColorTemperature { get; set; }
    public GradientState? Gradient { get; set; }
    public DynamicsUpdate? Dynamics { get; set; }
    public EffectUpdate? Effects { get; set; }
    public AlertUpdate? Alert { get; set; }

    public static LightUpdate Power(bool on) => new() { On = new OnState { On = on } };
    public static LightUpdate Brightness(double pct) => new() { On = new OnState { On = pct > 0 }, Dimming = new DimmingUpdate { Brightness = Math.Clamp(pct, 0, 100) } };
    public static LightUpdate Xy(double x, double y) => new() { On = new OnState { On = true }, Color = new ColorUpdate { Xy = new XyPoint(x, y) } };
    public static LightUpdate Mirek(int mirek) => new() { On = new OnState { On = true }, ColorTemperature = new ColorTemperatureUpdate { Mirek = mirek } };
    public static LightUpdate Identify() => new() { Alert = new AlertUpdate { Action = "breathe" } };
}

/// <summary>Partial PUT body for a grouped_light (a room, zone or the whole home).</summary>
public sealed class GroupedLightUpdate
{
    public OnState? On { get; set; }
    public DimmingUpdate? Dimming { get; set; }
    public ColorUpdate? Color { get; set; }
    public ColorTemperatureUpdate? ColorTemperature { get; set; }
    public DynamicsUpdate? Dynamics { get; set; }
    public AlertUpdate? Alert { get; set; }

    public static GroupedLightUpdate Power(bool on) => new() { On = new OnState { On = on } };
    public static GroupedLightUpdate Brightness(double pct) => new() { On = new OnState { On = pct > 0 }, Dimming = new DimmingUpdate { Brightness = Math.Clamp(pct, 0, 100) } };
}

public sealed class DimmingUpdate { public double Brightness { get; set; } }
public sealed class ColorUpdate { public XyPoint? Xy { get; set; } }
public sealed class ColorTemperatureUpdate { public int Mirek { get; set; } }
public sealed class DynamicsUpdate { public int? Duration { get; set; } public double? Speed { get; set; } }
public sealed class EffectUpdate { public string Effect { get; set; } = "no_effect"; }
public sealed class AlertUpdate { public string Action { get; set; } = "breathe"; }

public sealed class SceneRecall
{
    /// <summary>"active", "dynamic_palette" or "static".</summary>
    public string Action { get; set; } = "active";
    public int? Duration { get; set; }
    public DimmingUpdate? Dimming { get; set; }
}

public sealed class SceneUpdate
{
    public SceneRecall? Recall { get; set; }
    public Metadata? Metadata { get; set; }
    public List<SceneAction>? Actions { get; set; }
    public ScenePalette? Palette { get; set; }
    public double? Speed { get; set; }
    public bool? AutoDynamic { get; set; }
}

public sealed class SceneCreate
{
    public string Type { get; set; } = ResourceTypes.Scene;
    public Metadata Metadata { get; set; } = new();
    public ResourceRef Group { get; set; } = new("", ResourceTypes.Room);
    public List<SceneAction> Actions { get; set; } = [];
    public ScenePalette? Palette { get; set; }
    public double? Speed { get; set; }
    public bool? AutoDynamic { get; set; }
}

public sealed class GroupCreate
{
    public string Type { get; set; } = ResourceTypes.Zone;
    public Metadata Metadata { get; set; } = new();
    public List<ResourceRef> Children { get; set; } = [];
}

public sealed class GroupUpdate
{
    public Metadata? Metadata { get; set; }
    public List<ResourceRef>? Children { get; set; }
}

public sealed class EntertainmentConfigurationAction
{
    /// <summary>"start" or "stop".</summary>
    public string Action { get; set; } = "start";
}
