using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class FadeOutEffect() : FadeEffect(fadeIn: false)
{
    private static readonly RgbEffectInfo Info = Describe("Gradually fade out from specified color over a set duration", "Start Color");
    public override RgbEffect EffectType => RgbEffect.FadeOut;
    public Vector3 StartColor { get => Palette[0]; set => Palette[0] = value; }
    public override RgbEffectInfo GetInfo() => Info;
}
