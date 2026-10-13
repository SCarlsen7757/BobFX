using System.Numerics;
namespace BobFx.Core.Services.Effects;
internal static class EffectValidation
{
    public static void EnsureValid(IRgbEffect effect)
    {
        if (effect.Speed < TimeSpan.FromMilliseconds(10) || effect.Speed > TimeSpan.FromSeconds(60))
            throw new ArgumentException("Effect speed must be 10–60000 milliseconds.");
        var colors = effect switch
        {
            BlinkEffect e => new[] { e.Color1, e.Color2 },
            StrobeEffect e => new[] { e.PrimaryColor, e.SecondaryColor },
            SolidEffect e => new[] { e.Color },
            ScannerEffect e => new[] { e.Color },
            FadeInEffect e => new[] { e.TargetColor },
            FadeOutEffect e => new[] { e.StartColor },
            BreathingEffect e => new[] { e.Color },
            TwinkleEffect e => new[] { e.Color },
            _ => Array.Empty<Vector3>()
        };
        if (colors.Any(c => !FiniteUnit(c.X) || !FiniteUnit(c.Y) || !FiniteUnit(c.Z)))
            throw new ArgumentException("Color channels must be finite values between 0 and 1.");
        if (effect is BreathingEffect breathing &&
            (!FiniteUnit(breathing.MinBrightness) || !FiniteUnit(breathing.MaxBrightness)))
            throw new ArgumentException("Brightness must be finite and between 0 and 1.");
        if (effect is TwinkleEffect twinkle && (!FiniteUnit(twinkle.MinBrightness) || !FiniteUnit(twinkle.MaxBrightness) || !FiniteUnit(twinkle.TwinkleProbability)))
            throw new ArgumentException("Twinkle brightness and probability must be finite and between 0 and 1.");
        var validation = effect.Validate();
        if (!validation.IsValid) throw new ArgumentException(string.Join("; ", validation.Errors));
    }
    private static bool FiniteUnit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
}
