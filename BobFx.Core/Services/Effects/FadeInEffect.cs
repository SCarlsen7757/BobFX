using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class FadeInEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.FadeIn;

    public Vector3 TargetColor { get; set; } = new(1, 0, 0); // Red
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromSeconds(2);

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        var elapsed = DateTime.UtcNow - StartTime;

        if (elapsed >= FadeDuration)
        {
            // Fade complete - show target color at full brightness
            for (int i = 0; i < leds.Length; i++)
                leds[i] = TargetColor;
            return true; // Effect completed
        }

        // Calculate brightness based on elapsed time
        float progress = (float)(elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds);
        float brightness = Math.Clamp(progress, 0f, 1f);
        Vector3 fadedColor = TargetColor * brightness;

        for (int i = 0; i < leds.Length; i++)
            leds[i] = fadedColor;

        return false; // Still running
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Gradually fade in to specified color over a set duration",
        DefaultSpeed = TimeSpan.FromMilliseconds(50),
        ColorLabels = ["Target Color"],
        CustomParameters = new() { ["FadeDuration"] = typeof(TimeSpan) }
    };

    public override ValidationResult Validate()
    {
        var result = new ValidationResult();
        if (TargetColor == Vector3.Zero)
            result.AddError("Target color must be set");
        if (FadeDuration <= TimeSpan.Zero)
            result.AddError("Fade duration must be greater than zero");
        return result;
    }

    public override IRgbEffect Clone() => new FadeInEffect
    {
        Speed = Speed,
        TargetColor = TargetColor,
        FadeDuration = FadeDuration
    };
}
