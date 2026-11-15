using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>
/// Fluent builder for configuring RGB effects.
/// </summary>
public class RgbEffectBuilder
{
    private readonly IRgbEffectFactory factory;
    private RgbEffect effectType;
    private TimeSpan? speed;
    private readonly List<Vector3> colors = [];
    private TimeSpan? fadeDuration;
    private TimeSpan? breathDuration;
    private float? minBrightness;
    private float? maxBrightness;

    public RgbEffectBuilder(IRgbEffectFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// Set the effect type.
    /// </summary>
    public RgbEffectBuilder WithEffect(RgbEffect effectType)
    {
        this.effectType = effectType;
        return this;
    }

    /// <summary>
    /// Set the effect speed in milliseconds.
    /// </summary>
    public RgbEffectBuilder WithSpeed(int milliseconds)
    {
        speed = TimeSpan.FromMilliseconds(milliseconds);
        return this;
    }

    /// <summary>
    /// Add a color using hex string.
    /// </summary>
    public RgbEffectBuilder WithColor(string hexColor)
    {
        colors.Add(ColorHelper.HexToRgb(hexColor));
        return this;
    }

    /// <summary>
    /// Set fade duration in seconds for fade effects.
    /// </summary>
    public RgbEffectBuilder WithFadeDuration(double seconds)
    {
        fadeDuration = TimeSpan.FromSeconds(seconds);
        return this;
    }

    /// <summary>
    /// Set breath duration in seconds for breathing effects.
    /// </summary>
    public RgbEffectBuilder WithBreathDuration(double seconds)
    {
        breathDuration = TimeSpan.FromSeconds(seconds);
        return this;
    }

    /// <summary>
    /// Set minimum brightness for breathing effects (0.0 to 1.0).
    /// </summary>
    public RgbEffectBuilder WithMinBrightness(double brightness)
    {
        minBrightness = (float)brightness;
        return this;
    }

    /// <summary>
    /// Set maximum brightness for breathing effects (0.0 to 1.0).
    /// </summary>
    public RgbEffectBuilder WithMaxBrightness(double brightness)
    {
        maxBrightness = (float)brightness;
        return this;
    }

    /// <summary>
    /// Build and validate the effect.
    /// </summary>
    public IRgbEffect Build()
    {
        var effect = factory.Create(effectType);

        // Apply speed if set
        if (speed.HasValue)
            effect.Speed = speed.Value;

        // Apply colors based on effect type
        ApplyColors(effect);

        // Apply fade duration for fade effects
        if (fadeDuration.HasValue && effect is FadeInEffect fadeIn)
            fadeIn.FadeDuration = fadeDuration.Value;
        else if (fadeDuration.HasValue && effect is FadeOutEffect fadeOut)
            fadeOut.FadeDuration = fadeDuration.Value;

        // Apply breath duration for breathing effects
        if (breathDuration.HasValue && effect is BreathingEffect breathing)
        {
            breathing.BreathDuration = breathDuration.Value;
        }

        // Apply brightness parameters for breathing effects
        if (effect is BreathingEffect breathingEffect)
        {
            if (minBrightness.HasValue)
                breathingEffect.MinBrightness = minBrightness.Value;
            if (maxBrightness.HasValue)
                breathingEffect.MaxBrightness = maxBrightness.Value;
        }

        EffectValidation.EnsureValid(effect);

        return effect;
    }

    private void ApplyColors(IRgbEffect effect)
    {
        switch (effect)
        {
            case SolidEffect solid:
                if (colors.Count > 0) solid.Color = colors[0];
                break;

            case StrobeEffect strobe:
                if (colors.Count > 0) strobe.PrimaryColor = colors[0];
                if (colors.Count > 1) strobe.SecondaryColor = colors[1];
                break;

            case BlinkEffect blink:
                if (colors.Count > 0) blink.Color1 = colors[0];
                if (colors.Count > 1) blink.Color2 = colors[1];
                break;

            case ScannerEffect scanner:
                if (colors.Count > 0) scanner.Color = colors[0];
                break;

            case FadeInEffect fadeIn:
                if (colors.Count > 0) fadeIn.TargetColor = colors[0];
                break;

            case FadeOutEffect fadeOut:
                if (colors.Count > 0) fadeOut.StartColor = colors[0];
                break;

            case BreathingEffect breathing:
                if (colors.Count > 0) breathing.Color = colors[0];
                break;

            case TwinkleEffect twinkle:
                if (colors.Count > 0) twinkle.Color = colors[0];
                break;
        }
    }
}
