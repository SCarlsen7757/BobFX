using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>
/// Blinks between two colors at a specified rate.
/// </summary>
public class BlinkEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Blink;

    public Vector3 Color1 { get; set; } = new(1, 0, 0); // Red
    public Vector3 Color2 { get; set; } = new(0, 0, 1); // Blue

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        // Alternate between the two colors
        Vector3 currentColor = (Step % 2 == 0) ? Color1 : Color2;

        for (int i = 0; i < leds.Length; i++)
            leds[i] = currentColor;

        Step++;
        return false; // Never completes - continuous effect
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 2,
        OptionalColors = 0,
        Description = "Alternates between two colors",
        DefaultSpeed = TimeSpan.FromMilliseconds(500),
        ColorLabels = ["Color 1", "Color 2"]
    };

    public override ValidationResult Validate()
    {
        var result = new ValidationResult();
        if (Color1 == Vector3.Zero)
            result.AddError("Color 1 must be set to a non-black color");
        if (Color2 == Vector3.Zero)
            result.AddError("Color 2 must be set to a non-black color");
        return result;
    }

    public override IRgbEffect Clone() => new BlinkEffect
    {
        Speed = Speed,
        Color1 = Color1,
        Color2 = Color2
    };
}
