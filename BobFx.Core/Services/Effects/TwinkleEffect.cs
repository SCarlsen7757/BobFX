using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class TwinkleEffect() : RgbEffectBase(new Vector3(1, 1, 1))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 1, Description = "Random twinkling effect with LEDs fading in and out independently",
        DefaultSpeed = TimeSpan.FromMilliseconds(100), ColorLabels = ["Color"]
    };
    /// <summary>How far an LED moves toward its next level in one frame when it does not jump there.</summary>
    private const float FadeSpeed = 0.05f;

    public override RgbEffect EffectType => RgbEffect.Twinkle;
    public Vector3 Color { get => Palette[0]; set => Palette[0] = value; }
    /// <summary>The chance, per LED and frame, that the LED jumps to a new level instead of drifting.</summary>
    public float TwinkleProbability { get; set; } = 0.1f;
    public float MaxBrightness { get; set; } = 1.0f;
    public float MinBrightness { get; set; } = 0.0f;
    public override RgbEffectInfo GetInfo() => Info;

    private readonly Random random = new();
    private float[] levels = [];

    private float RandomLevel() => random.NextSingle() * (MaxBrightness - MinBrightness) + MinBrightness;

    public override void Initialize(int ledCount, TimeProvider clock)
    {
        base.Initialize(ledCount, clock);
        levels = new float[ledCount];
        for (var i = 0; i < ledCount; i++) levels[i] = RandomLevel();
    }

    public override bool Apply(Span<Vector3> leds)
    {
        for (var i = 0; i < leds.Length && i < levels.Length; i++)
        {
            var target = RandomLevel();
            levels[i] = Math.Clamp(random.NextSingle() < TwinkleProbability ? target : levels[i] + (target - levels[i]) * FadeSpeed, MinBrightness, MaxBrightness);
            leds[i] = Color * levels[i];
        }
        return false;
    }

    protected override IEnumerable<string> Problems()
    {
        if (!IsUnit(MinBrightness) || !IsUnit(MaxBrightness) || !IsUnit(TwinkleProbability))
            yield return "Twinkle brightness and probability must be finite and between 0 and 1";
        else if (MaxBrightness <= MinBrightness) yield return "Max brightness must be greater than min brightness";
    }
}
