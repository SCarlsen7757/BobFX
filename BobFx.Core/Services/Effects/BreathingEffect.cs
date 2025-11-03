using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class BreathingEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Breathing;

    public Vector3 Color { get; set; } = new(1, 0, 0); // Red
    public TimeSpan BreathDuration { get; set; } = TimeSpan.FromSeconds(2);
    public float MinBrightness { get; set; } = 0f;
    public float MaxBrightness { get; set; } = 1f;

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        var elapsed = DateTime.UtcNow - StartTime;
        var cycleProgress = (elapsed.TotalMilliseconds % (BreathDuration.TotalMilliseconds * 2)) / (BreathDuration.TotalMilliseconds * 2);

        // Calculate brightness (0 to 1 to 0)
        // Fade in (0 to 1) then out (1 to 0)
        float normalizedBrightness = (float)(cycleProgress < 0.5
            ? cycleProgress * 2
            : (1 - cycleProgress) * 2);

        // Map from 0-1 to MinBrightness-MaxBrightness
        float brightness = MinBrightness + (normalizedBrightness * (MaxBrightness - MinBrightness));
        brightness = Math.Clamp(brightness, MinBrightness, MaxBrightness);

        Vector3 fadedColor = Color * brightness;

        for (int i = 0; i < leds.Length; i++)
            leds[i] = fadedColor;

        return false; // Never completes
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Breathing effect that fades between configurable minimum and maximum brightness",
        DefaultSpeed = TimeSpan.FromMilliseconds(50),
        ColorLabels = ["Color"],
        CustomParameters = new()
        {
            ["BreathDuration"] = typeof(TimeSpan),
            ["MinBrightness"] = typeof(float),
            ["MaxBrightness"] = typeof(float)
        }
    };

    public override ValidationResult Validate()
    {
        if (Color == Vector3.Zero)
            return ValidationResult.Fail("Color must be set");
        if (BreathDuration <= TimeSpan.Zero)
            return ValidationResult.Fail("Breath duration must be greater than zero");
        if (MinBrightness < 0f || MinBrightness > 1f)
            return ValidationResult.Fail("Minimum brightness must be between 0 and 1");
        if (MaxBrightness < 0f || MaxBrightness > 1f)
            return ValidationResult.Fail("Maximum brightness must be between 0 and 1");
        if (MinBrightness >= MaxBrightness)
            return ValidationResult.Fail("Minimum brightness must be less than maximum brightness");
        return ValidationResult.Success();
    }

    public override IRgbEffect Clone() => new BreathingEffect
    {
        Speed = Speed,
        Color = Color,
        BreathDuration = BreathDuration,
        MinBrightness = MinBrightness,
        MaxBrightness = MaxBrightness
    };
}
