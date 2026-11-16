using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class RainbowEffect : RgbEffectBase
{
    private static readonly RgbEffectInfo Info = new() { Description = "Animated rainbow across all LEDs", DefaultSpeed = TimeSpan.FromMilliseconds(50) };
    public override RgbEffect EffectType => RgbEffect.Rainbow;
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        for (var i = 0; i < leds.Length; i++)
            leds[i] = ColorHelper.HsvToRgb((i * 360 / leds.Length + Step) % 360, 1.0f, 1.0f);
        Step = (Step + 4) % 360;
        return false;
    }
}
