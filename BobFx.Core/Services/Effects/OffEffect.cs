using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class OffEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Off;

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        ClearLeds(leds);
        return true; // Always runs (does nothing)
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 0,
        OptionalColors = 0,
        Description = "Turn off all LEDs",
        DefaultSpeed = TimeSpan.FromMilliseconds(100)
    };

    public override IRgbEffect Clone() => new OffEffect { Speed = Speed };
}
