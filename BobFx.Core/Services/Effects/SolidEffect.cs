using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class SolidEffect() : RgbEffectBase(new Vector3(1, 0, 0))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 1, Description = "Solid color on all LEDs", DefaultSpeed = TimeSpan.FromMilliseconds(100), ColorLabels = ["Color"]
    };
    public override RgbEffect EffectType => RgbEffect.Solid;
    public Vector3 Color { get => Palette[0]; set => Palette[0] = value; }
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        leds.Fill(Color);
        return false;
    }
}
