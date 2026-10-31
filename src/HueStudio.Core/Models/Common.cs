using System.Text.Json.Serialization;

namespace HueStudio.Core.Models;

/// <summary>Reference to another resource on the bridge (Hue API v2 "resource identifier").</summary>
public sealed record ResourceRef(
    [property: JsonPropertyName("rid")] string Rid,
    [property: JsonPropertyName("rtype")] string Rtype);

public sealed class Metadata
{
    public string? Name { get; set; }
    public string? Archetype { get; set; }
    /// <summary>Scene image reference (scenes only).</summary>
    public ResourceRef? Image { get; set; }
}

public sealed class OnState
{
    public bool On { get; set; }
}

public sealed class Dimming
{
    /// <summary>Brightness percentage 0-100 (the bridge clamps to the light's minimum).</summary>
    public double Brightness { get; set; }
    public double? MinDimLevel { get; set; }
}

public sealed record XyPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

public sealed class Gamut
{
    public XyPoint Red { get; set; } = new(0.6915, 0.3083);
    public XyPoint Green { get; set; } = new(0.17, 0.7);
    public XyPoint Blue { get; set; } = new(0.1532, 0.0475);
}

public sealed class ColorState
{
    public XyPoint? Xy { get; set; }
    public Gamut? Gamut { get; set; }
    /// <summary>"A", "B", "C" or "other".</summary>
    public string? GamutType { get; set; }
}

public sealed class MirekSchema
{
    public int MirekMinimum { get; set; } = 153;
    public int MirekMaximum { get; set; } = 500;
}

public sealed class ColorTemperatureState
{
    public int? Mirek { get; set; }
    public bool MirekValid { get; set; }
    public MirekSchema? MirekSchema { get; set; }
}

public sealed class GradientState
{
    public List<GradientPoint> Points { get; set; } = [];
    public int? PointsCapable { get; set; }
}

public sealed class GradientPoint
{
    public ColorState Color { get; set; } = new();
}

public sealed class Dynamics
{
    public string? Status { get; set; }
    public double? Speed { get; set; }
    public bool? SpeedValid { get; set; }
}

public sealed class EffectsState
{
    public string? Effect { get; set; }
    public string? Status { get; set; }
    public List<string>? EffectValues { get; set; }
}

public sealed class ProductData
{
    public string? ModelId { get; set; }
    public string? ManufacturerName { get; set; }
    public string? ProductName { get; set; }
    public string? ProductArchetype { get; set; }
    public bool? Certified { get; set; }
    public string? SoftwareVersion { get; set; }
}
