using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>What every effect shares: its speed and colors, the clock it runs on, and the checks all effects pass.</summary>
public abstract class RgbEffectBase(params Vector3[] colors) : IRgbEffect
{
    public const int MinSpeedMilliseconds = 10, MaxSpeedMilliseconds = 60000;

    /// <summary>The effect's colors; subclasses name them through properties.</summary>
    protected Vector3[] Palette { get; private set; } = colors;

    public abstract RgbEffect EffectType { get; }
    public TimeSpan Speed { get; set; } = TimeSpan.FromMilliseconds(100);
    public IReadOnlyList<Vector3> Colors => Palette;
    public void SetColor(int index, Vector3 color) => Palette[index] = color;

    protected int Step { get; set; }
    protected long StartTimestamp { get; private set; }
    protected TimeProvider Clock { get; private set; } = TimeProvider.System;

    public virtual void Initialize(int ledCount, TimeProvider clock)
    {
        Clock = clock;
        Reset();
    }

    public abstract bool Apply(Span<Vector3> leds);

    /// <summary>Back to the first frame.</summary>
    protected virtual void Reset()
    {
        Step = 0;
        StartTimestamp = Clock.GetTimestamp();
    }

    public abstract RgbEffectInfo GetInfo();

    public void EnsureValid()
    {
        if (Speed < TimeSpan.FromMilliseconds(MinSpeedMilliseconds) || Speed > TimeSpan.FromMilliseconds(MaxSpeedMilliseconds))
            throw new ArgumentException($"Effect speed must be {MinSpeedMilliseconds}–{MaxSpeedMilliseconds} milliseconds.");
        if (Palette.Any(color => !IsUnit(color.X) || !IsUnit(color.Y) || !IsUnit(color.Z)))
            throw new ArgumentException("Color channels must be finite values between 0 and 1.");
        var info = GetInfo();
        // A required color that is black would show nothing at all.
        var problems = Enumerable.Range(0, Math.Min(info.RequiredColors, Palette.Length)).Where(index => Palette[index] == Vector3.Zero)
            .Select(index => $"{info.ColorLabels.ElementAtOrDefault(index) ?? "Color"} must be set").Concat(Problems()).ToList();
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
    }
    /// <summary>What is wrong with the settings only this effect has.</summary>
    protected virtual IEnumerable<string> Problems() => [];
    protected static bool IsUnit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    public IRgbEffect Clone()
    {
        // Settings are values; the colors are the one array a copy must not share.
        var copy = (RgbEffectBase)MemberwiseClone();
        copy.Palette = [.. Palette];
        return copy;
    }
}
