using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class RainbowEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Rainbow;

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        for (int i = 0; i < leds.Length; i++)
        {
            int hue = (i * 360 / leds.Length + Step) % 360;
            leds[i] = HsvToRgb(hue, 1.0f, 1.0f);
        }

        Step = (Step + 4) % 360;
        return false; // Never completes
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 0,
        OptionalColors = 0,
        Description = "Animated rainbow across all LEDs",
        DefaultSpeed = TimeSpan.FromMilliseconds(50)
    };

    public override IRgbEffect Clone() => new RainbowEffect { Speed = Speed };
}
