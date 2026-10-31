using HueStudio.Core.Models;

namespace HueStudio.Core.Color;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb FromHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) throw new FormatException($"Expected #RRGGBB, got '{hex}'.");
        return new Rgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..], 16));
    }
}

/// <summary>
/// Colour conversions between screen colours (sRGB) and what Hue lights speak (CIE xy + brightness,
/// or mirek for white). Based on the Philips "RGB to xy" guidance, with gamut clamping so a colour
/// the bulb can't show maps to the nearest one it can.
/// </summary>
public static class ColorMath
{
    public static readonly Gamut GamutA = new() { Red = new(0.704, 0.296), Green = new(0.2151, 0.7106), Blue = new(0.138, 0.08) };
    public static readonly Gamut GamutB = new() { Red = new(0.675, 0.322), Green = new(0.409, 0.518), Blue = new(0.167, 0.04) };
    public static readonly Gamut GamutC = new() { Red = new(0.6915, 0.3083), Green = new(0.17, 0.7), Blue = new(0.1532, 0.0475) };

    public static Gamut GamutFor(ColorState? color) =>
        color?.Gamut ?? (color?.GamutType switch { "A" => GamutA, "B" => GamutB, _ => GamutC });

    public const int MirekMin = 153; // ~6500 K
    public const int MirekMax = 500; // 2000 K

    public static int KelvinToMirek(double kelvin) => (int)Math.Round(1_000_000 / Math.Clamp(kelvin, 1000, 40000));
    public static int MirekToKelvin(int mirek) => (int)Math.Round(1_000_000.0 / Math.Max(mirek, 1));

    /// <summary>sRGB to CIE xy, clamped to the gamut if given. Returns xy plus relative luminance Y (0-1).</summary>
    public static (XyPoint Xy, double Luminance) RgbToXy(Rgb rgb, Gamut? gamut = null)
    {
        double r = Linear(rgb.R / 255.0), g = Linear(rgb.G / 255.0), b = Linear(rgb.B / 255.0);

        var X = r * 0.4124 + g * 0.3576 + b * 0.1805;
        var Y = r * 0.2126 + g * 0.7152 + b * 0.0722;
        var Z = r * 0.0193 + g * 0.1192 + b * 0.9505;
        var sum = X + Y + Z;

        // Black has no chromaticity: return D65 white point.
        var xy = sum <= 0 ? new XyPoint(0.3127, 0.3290) : new XyPoint(X / sum, Y / sum);
        if (gamut is not null) xy = ClampToGamut(xy, gamut);
        return (new XyPoint(Math.Round(xy.X, 4), Math.Round(xy.Y, 4)), Y);
    }

    /// <summary>CIE xy (+ brightness 0-100) to the closest sRGB colour, for drawing swatches.</summary>
    public static Rgb XyToRgb(XyPoint xy, double brightnessPct = 100)
    {
        var y = Math.Max(xy.Y, 1e-6);
        const double Y = 1.0;
        var X = Y / y * xy.X;
        var Z = Y / y * (1 - xy.X - xy.Y);

        var r = X * 3.2406 - Y * 1.5372 - Z * 0.4986;
        var g = -X * 0.9689 + Y * 1.8758 + Z * 0.0415;
        var b = X * 0.0557 - Y * 0.2040 + Z * 1.0570;

        // Bring out-of-range values back in while keeping hue.
        var min = Math.Min(r, Math.Min(g, b));
        if (min < 0) { r -= min; g -= min; b -= min; }
        var max = Math.Max(r, Math.Max(g, b));
        if (max > 0) { r /= max; g /= max; b /= max; }

        var scale = Math.Clamp(brightnessPct, 0, 100) / 100.0;
        return new Rgb(ToByte(Gamma(r) * scale), ToByte(Gamma(g) * scale), ToByte(Gamma(b) * scale));
    }

    /// <summary>Approximate sRGB for a white colour temperature (Tanner Helland's fit), for swatches.</summary>
    public static Rgb MirekToRgb(int mirek)
    {
        var t = MirekToKelvin(mirek) / 100.0;
        double r, g, b;
        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
            b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = 255;
        }
        return new Rgb(ToByte(r / 255), ToByte(g / 255), ToByte(b / 255));
    }

    /// <summary>What a light looks like right now, for UI tiles. Off lights are dark grey.</summary>
    public static Rgb DisplayColor(Light light)
    {
        if (!light.IsOn) return new Rgb(0x2A, 0x2A, 0x2E);
        var bri = Math.Max(light.Dimming?.Brightness ?? 100, 25); // keep dim lights visible on screen
        if (light.ColorTemperature?.MirekValid == true && light.ColorTemperature.Mirek is int m)
            return Scale(MirekToRgb(m), bri);
        if (light.Color?.Xy is { } xy) return XyToRgb(xy, bri);
        return Scale(MirekToRgb(366), bri); // plain dimmable white
    }

    public static Rgb Scale(Rgb c, double pct)
    {
        var s = Math.Clamp(pct, 0, 100) / 100.0;
        return new Rgb((byte)(c.R * s), (byte)(c.G * s), (byte)(c.B * s));
    }

    public static bool IsInGamut(XyPoint p, Gamut g)
    {
        var d1 = Cross(p, g.Red, g.Green);
        var d2 = Cross(p, g.Green, g.Blue);
        var d3 = Cross(p, g.Blue, g.Red);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    public static XyPoint ClampToGamut(XyPoint p, Gamut g)
    {
        if (IsInGamut(p, g)) return p;
        var candidates = new[]
        {
            ClosestOnSegment(g.Red, g.Green, p),
            ClosestOnSegment(g.Green, g.Blue, p),
            ClosestOnSegment(g.Blue, g.Red, p),
        };
        return candidates.MinBy(c => Dist2(c, p))!;
    }

    private static double Cross(XyPoint p, XyPoint a, XyPoint b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    private static XyPoint ClosestOnSegment(XyPoint a, XyPoint b, XyPoint p)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / (abx * abx + aby * aby);
        t = Math.Clamp(t, 0, 1);
        return new XyPoint(a.X + t * abx, a.Y + t * aby);
    }

    private static double Dist2(XyPoint a, XyPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    private static double Linear(double c) => c > 0.04045 ? Math.Pow((c + 0.055) / 1.055, 2.4) : c / 12.92;
    private static double Gamma(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}
