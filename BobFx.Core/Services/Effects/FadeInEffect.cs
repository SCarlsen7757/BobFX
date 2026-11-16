using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class FadeInEffect() : FadeEffect(fadeIn: true)
{
    private static readonly RgbEffectInfo Info = Describe("Gradually fade in to specified color over a set duration", "Target Color");
    public override RgbEffect EffectType => RgbEffect.FadeIn;
    public Vector3 TargetColor { get => Palette[0]; set => Palette[0] = value; }
    public override RgbEffectInfo GetInfo() => Info;
}
