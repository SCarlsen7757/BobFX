using System.Globalization;
using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>Conversions between the color forms in use: #RRGGBB text, bytes, and vectors with channels from 0 to 1.</summary>
public static class ColorHelper
{
    /// <summary>Reads #RRGGBB. This is the one place that decides what a color in a request or in settings may look like.</summary>
    public static bool TryParseHex(string? hex, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        return hex is { Length: 7 } && hex[0] == '#' &&
            byte.TryParse(hex.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out red) &&
            byte.TryParse(hex.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out green) &&
            byte.TryParse(hex.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out blue);
    }

    public static Vector3 HexToRgb(string hex) => TryParseHex(hex, out var red, out var green, out var blue)
        ? new Vector3(red / 255f, green / 255f, blue / 255f)
        : throw new ArgumentException("Colors must use #RRGGBB format.", nameof(hex));

    public static string RgbToHex(Vector3 color)
    {
        var (red, green, blue) = ToRgbBytes(color);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    public static (byte r, byte g, byte b) ToRgbBytes(Vector3 color) =>
        ((byte)(Math.Clamp(color.X, 0f, 1f) * 255), (byte)(Math.Clamp(color.Y, 0f, 1f) * 255), (byte)(Math.Clamp(color.Z, 0f, 1f) * 255));

    /// <summary>A color from its hue in degrees (0 up to 360), saturation and value.</summary>
    public static Vector3 HsvToRgb(float hue, float saturation, float value)
    {
        var chroma = value * saturation;
        var second = chroma * (1 - MathF.Abs(hue / 60 % 2 - 1));
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, second, 0f),
            < 120 => (second, chroma, 0f),
            < 180 => (0f, chroma, second),
            < 240 => (0f, second, chroma),
            < 300 => (second, 0f, chroma),
            _ => (chroma, 0f, second)
        };
        var lift = value - chroma;
        return new Vector3(red + lift, green + lift, blue + lift);
    }
}
