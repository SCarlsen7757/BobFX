using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>
/// Helper methods for color conversion and manipulation.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Convert hex color string to RGB vector.
    /// </summary>
    public static Vector3 HexToRgb(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new ArgumentException("Hex color cannot be null or empty", nameof(hex));

        hex = hex.TrimStart('#');

        if (hex.Length != 6)
            throw new ArgumentException("Hex color must be 6 characters long", nameof(hex));

        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);

        // Normalize to 0..1
        return new Vector3(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// Convert RGB vector to hex color string.
    /// </summary>
    public static string RgbToHex(Vector3 color)
    {
        int r = (int)(Math.Clamp(color.X, 0f, 1f) * 255);
        int g = (int)(Math.Clamp(color.Y, 0f, 1f) * 255);
        int b = (int)(Math.Clamp(color.Z, 0f, 1f) * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// Convert RGB bytes to normalized vector.
    /// </summary>
    public static Vector3 FromRgb(byte r, byte g, byte b)
    {
        return new Vector3(r / 255f, g / 255f, b / 255f);
    }

    /// <summary>
    /// Convert normalized RGB vector to bytes.
    /// </summary>
    public static (byte r, byte g, byte b) ToRgbBytes(Vector3 color)
    {
        byte r = (byte)(Math.Clamp(color.X, 0f, 1f) * 255);
        byte g = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255);
        byte b = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255);
        return (r, g, b);
    }
}
