using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class StrobeEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Strobe;

    public Vector3 PrimaryColor { get; set; } = new(1, 0, 0); // Red
    public Vector3 SecondaryColor { get; set; } = new(0, 1, 0); // Green

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        Vector3 color = (Step % 2 == 0) ? SecondaryColor : PrimaryColor;

        for (int i = 0; i < leds.Length; i++)
            leds[i] = color;

        Step = (Step + 1) % 2;
        return false; // Never completes
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 1,
        Description = "Alternates between two colors",
        DefaultSpeed = TimeSpan.FromMilliseconds(250),
        ColorLabels = ["Primary Color", "Secondary Color"]
    };

    public override ValidationResult Validate()
    {
        var result = new ValidationResult();
        if (PrimaryColor == Vector3.Zero)
            result.AddError("Primary color must be set");
        return result;
    }

    public override IRgbEffect Clone() => new StrobeEffect
    {
        Speed = Speed,
        PrimaryColor = PrimaryColor,
        SecondaryColor = SecondaryColor
    };
}
