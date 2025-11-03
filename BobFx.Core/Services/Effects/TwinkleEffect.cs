using System.Numerics;

namespace BobFx.Core.Services.Effects;

public class TwinkleEffect : RgbEffectBase
{
    public override RgbEffect EffectType => RgbEffect.Twinkle;

    public Vector3 Color { get; set; } = new(1, 1, 1); // White
    public float TwinkleProbability { get; set; } = 0.1f; // 10% chance per LED per frame
    public float MaxBrightness { get; set; } = 1.0f;
    public float MinBrightness { get; set; } = 0.0f;

    private readonly Random random = new();
    private float[]? ledBrightness;

    public override void Initialize(int ledCount)
    {
        base.Initialize(ledCount);
        ledBrightness = new float[ledCount];

        // Initialize with random brightness values
        for (int i = 0; i < ledCount; i++)
        {
            ledBrightness[i] = random.NextSingle() * (MaxBrightness - MinBrightness) + MinBrightness;
        }
    }

    public override bool Apply(Span<Vector3> leds, CancellationToken token)
    {
        if (ledBrightness == null)
        {
            Initialize(leds.Length);
        }

        for (int i = 0; i < leds.Length; i++)
        {
            // Random chance to change brightness
            if (random.NextSingle() < TwinkleProbability)
            {
                ledBrightness![i] = random.NextSingle() * (MaxBrightness - MinBrightness) + MinBrightness;
            }
            else
            {
                // Slowly fade toward a random target
                float target = random.NextSingle() * (MaxBrightness - MinBrightness) + MinBrightness;
                float fadeSpeed = 0.05f;
                ledBrightness![i] += (target - ledBrightness[i]) * fadeSpeed;
            }

            ledBrightness![i] = Math.Clamp(ledBrightness[i], MinBrightness, MaxBrightness);
            leds[i] = Color * ledBrightness[i];
        }

        return false; // Never completes
    }

    public override void Reset()
    {
        base.Reset();
        ledBrightness = null;
    }

    public override RgbEffectInfo GetInfo() => new()
    {
        RequiredColors = 1,
        OptionalColors = 0,
        Description = "Random twinkling effect with LEDs fading in and out independently",
        DefaultSpeed = TimeSpan.FromMilliseconds(100),
        ColorLabels = ["Color"]
    };

    public override ValidationResult Validate()
    {
        if (Color == Vector3.Zero)
            return ValidationResult.Fail("Color must be set");
        if (TwinkleProbability < 0 || TwinkleProbability > 1)
            return ValidationResult.Fail("Twinkle probability must be between 0 and 1");
        if (MaxBrightness <= MinBrightness)
            return ValidationResult.Fail("Max brightness must be greater than min brightness");
        return ValidationResult.Success();
    }

    public override IRgbEffect Clone() => new TwinkleEffect
    {
        Speed = Speed,
        Color = Color,
        TwinkleProbability = TwinkleProbability,
        MaxBrightness = MaxBrightness,
        MinBrightness = MinBrightness
    };
}
