using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class BlinkEffect() : RgbEffectBase(new Vector3(1, 0, 0), new Vector3(0, 0, 1))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 2, Description = "Alternates between two colors", DefaultSpeed = TimeSpan.FromMilliseconds(500), ColorLabels = ["Color 1", "Color 2"]
    };
    public override RgbEffect EffectType => RgbEffect.Blink;
    public Vector3 Color1 { get => Palette[0]; set => Palette[0] = value; }
    public Vector3 Color2 { get => Palette[1]; set => Palette[1] = value; }
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        leds.Fill(Step == 0 ? Color1 : Color2);
        Step = 1 - Step;
        return false;
    }
}
