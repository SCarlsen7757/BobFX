using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class ScannerEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Scanner;

    public Vector3 Color { get; set; } = new(1, 0, 0); // Red
    private int direction = 1; // 1 = forward, -1 = backward

    public override void Reset()
    {
        base.Reset();
        direction = 1;
    }

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        ClearLeds(leds);

        int pos = Step % leds.Length;
        leds[pos] = Color;

        Step += direction;

        if (Step >= leds.Length - 1)
            direction = -1;
        else if (Step <= 0)
            direction = 1;

        return false; // Never completes
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Single LED moving back and forth",
        DefaultSpeed = TimeSpan.FromMilliseconds(100),
        ColorLabels = ["Scanner Color"]
    };

    public override ValidationResult Validate()
    {
        if (Color == Vector3.Zero)
            return ValidationResult.Fail("Color must be set");
        return ValidationResult.Success();
    }

    public override IRgbEffect Clone() => new ScannerEffect
    {
        Speed = Speed,
        Color = Color
    };
}
