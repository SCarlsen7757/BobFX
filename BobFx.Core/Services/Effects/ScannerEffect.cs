using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class ScannerEffect() : RgbEffectBase(new Vector3(1, 0, 0))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 1, Description = "Single LED moving back and forth", DefaultSpeed = TimeSpan.FromMilliseconds(100), ColorLabels = ["Scanner Color"]
    };
    public override RgbEffect EffectType => RgbEffect.Scanner;
    public Vector3 Color { get => Palette[0]; set => Palette[0] = value; }
    public override RgbEffectInfo GetInfo() => Info;

    private int direction = 1; // 1 = forward, -1 = backward

    protected override void Reset()
    {
        base.Reset();
        direction = 1;
    }

    public override bool Apply(Span<Vector3> leds)
    {
        leds.Clear();
        leds[Step % leds.Length] = Color;
        Step += direction;
        if (Step >= leds.Length - 1) direction = -1;
        else if (Step <= 0) direction = 1;
        return false;
    }
}
