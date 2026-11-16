namespace BobFx.Core.Services;

/// <summary>A color with channels from 0 to 255 that may overshoot while layers are added up.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb operator *(Rgb color, double gain) => new(color.R * gain, color.G * gain, color.B * gain);
    public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    public static Rgb Mix(Rgb a, Rgb b, double amount) => a * (1 - amount) + b * amount;
}

/// <summary>The few curves that the board light show and the BUSY Bar screens are both drawn with.</summary>
internal static class ShowMath
{
    internal static readonly Rgb White = new(255, 255, 255);

    internal static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    /// <summary>0 to 1 with a soft start and a soft landing.</summary>
    internal static double Smooth(double value) { var v = Clamp01(value); return v * v * (3 - 2 * v); }
    /// <summary>The part after the decimal point, also for negative values: where in a repeating cycle a value is.</summary>
    internal static double Frac(double value) => value - Math.Floor(value);

    /// <summary>The color at a position on a gradient given by its stops, in rising order.</summary>
    internal static Rgb Ramp(double at, params (double At, Rgb Color)[] stops)
    {
        if (at <= stops[0].At) return stops[0].Color;
        for (var i = 1; i < stops.Length; i++)
            if (at <= stops[i].At)
                return Rgb.Mix(stops[i - 1].Color, stops[i].Color, (at - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
        return stops[^1].Color;
    }
}

/// <summary>When the moments of a game happen. The boards and the BUSY Bar take them from here, which is what
/// keeps the two on the same beat.</summary>
internal static class ShowTiming
{
    /// <summary>The countdown's stages as shares of the preparation: a scanner, then the players' ends, then everything.</summary>
    public const double ScannerUntil = 0.6, PlayerEndsUntil = 0.8;
    // The ending opens with three flares: each is lit for a sixth of the flare time and dark for the next sixth.
    private const int FlareSixths = 6;

    /// <summary>The ending is the flares followed by the comet.</summary>
    public static double EndingSeconds(AnimationSettings timing) => timing.FlashSeconds + timing.DotSeconds;

    /// <summary>Where in the flares a moment is: which flare (0, 1 or 2), whether it is lit, how many seconds
    /// ago it lit up, and how far from 0 to 1 it has spread.</summary>
    public static (int Number, bool Lit, double Since, double Spread) Flare(double elapsed, double flashSeconds)
    {
        var sixth = flashSeconds / FlareSixths;
        var interval = (int)(elapsed / sixth);
        var since = elapsed - interval * sixth;
        return (interval / 2, interval % 2 == 0, since, ShowMath.Clamp01(since / sixth));
    }
}
