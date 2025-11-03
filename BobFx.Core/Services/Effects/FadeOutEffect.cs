using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class FadeOutEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.FadeOut;

    public Vector3 StartColor { get; set; } = new(1, 0, 0); // Red
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromSeconds(2);

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        var elapsed = DateTime.UtcNow - StartTime;

        if (elapsed >= FadeDuration)
        {
            // Fade complete - turn off all LEDs
            ClearLeds(leds);
            return true; // Effect completed
        }

        // Calculate brightness based on elapsed time (inverse of fade in)
        float progress = (float)(elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds);
        float brightness = Math.Clamp(1f - progress, 0f, 1f);
        Vector3 fadedColor = StartColor * brightness;

        for (int i = 0; i < leds.Length; i++)
            leds[i] = fadedColor;

        return false; // Still running
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Gradually fade out from specified color over a set duration",
        DefaultSpeed = TimeSpan.FromMilliseconds(50),
        ColorLabels = ["Start Color"],
        CustomParameters = new() { ["FadeDuration"] = typeof(TimeSpan) }
    };

    public override ValidationResult Validate()
    {
        var result = new ValidationResult();
        if (StartColor == Vector3.Zero)
            result.AddError("Start color must be set");
        if (FadeDuration <= TimeSpan.Zero)
            result.AddError("Fade duration must be greater than zero");
        return result;
    }

    public override IRgbEffect Clone() => new FadeOutEffect
    {
        Speed = Speed,
        StartColor = StartColor,
        FadeDuration = FadeDuration
    };
}
