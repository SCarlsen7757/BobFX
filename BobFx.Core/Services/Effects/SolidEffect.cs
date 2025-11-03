using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class SolidEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Solid;

    public Vector3 Color { get; set; } = new(1, 0, 0); // Default red

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        for (int i = 0; i < leds.Length; i++)
            leds[i] = Color;

        return false; // Never completes
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Solid color on all LEDs",
        DefaultSpeed = TimeSpan.FromMilliseconds(100),
        ColorLabels = ["Color"]
    };

    public override ValidationResult Validate()
    {
        if (Color == Vector3.Zero)
            return ValidationResult.Fail("Color must be set");
        return ValidationResult.Success();
    }

    public override IRgbEffect Clone() => new SolidEffect
    {
        Speed = Speed,
        Color = Color
    };
}
