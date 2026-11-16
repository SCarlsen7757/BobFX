using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class OffEffect : RgbEffectBase
{
    private static readonly RgbEffectInfo Info = new() { Description = "Turn off all LEDs", DefaultSpeed = TimeSpan.FromMilliseconds(100) };
    public override RgbEffect EffectType => RgbEffect.Off;
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        leds.Clear();
        return true;
    }
}
