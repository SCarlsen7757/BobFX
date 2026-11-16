using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>Like <see cref="BlinkEffect"/>, but it starts on its second color, which may be left black for a flash.</summary>
public sealed class StrobeEffect() : RgbEffectBase(new Vector3(1, 0, 0), new Vector3(0, 1, 0))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 1, OptionalColors = 1, Description = "Alternates between two colors", DefaultSpeed = TimeSpan.FromMilliseconds(250),
        ColorLabels = ["Primary Color", "Secondary Color"]
    };
    public override RgbEffect EffectType => RgbEffect.Strobe;
    public Vector3 PrimaryColor { get => Palette[0]; set => Palette[0] = value; }
    public Vector3 SecondaryColor { get => Palette[1]; set => Palette[1] = value; }
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        leds.Fill(Step == 0 ? SecondaryColor : PrimaryColor);
        Step = 1 - Step;
        return false;
    }
}
