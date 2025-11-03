using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>
/// Base class for all RGB effects providing common functionality.
/// </summary>
public abstract class RgbEffectBase : IRgbEffect
{
    public abstract RgbEffect EffectType { get; }
    public TimeSpan Speed { get; set; } = TimeSpan.FromMilliseconds(100);

    protected int Step { get; set; }
    protected DateTime StartTime { get; set; }
    protected int LedCount { get; private set; }

    public virtual void Initialize(int ledCount)
    {
        LedCount = ledCount;
        Reset();
    }

    public abstract bool Apply(Span<Vector3> leds, CancellationToken token);

    public virtual void Reset()
    {
        Step = 0;
        StartTime = DateTime.UtcNow;
    }

    public abstract RgbEffectInfo GetInfo();

    public virtual ValidationResult Validate()
    {
        return ValidationResult.Success();
    }

    public abstract IRgbEffect Clone();

    protected void ClearLeds(Span<Vector3> leds)
    {
        for (int i = 0; i < leds.Length; i++)
            leds[i] = Vector3.Zero;
    }

    protected static Vector3 HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - MathF.Abs((h / 60) % 2 - 1));
        float m = v - c;
        float r1, g1, b1;

        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return new Vector3(r1 + m, g1 + m, b1 + m);
    }
}
