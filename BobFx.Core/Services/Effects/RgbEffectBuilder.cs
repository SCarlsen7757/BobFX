using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>Collects what a request asks of an effect and makes the effect. Settings the chosen effect does not
/// have are ignored; the renderer validates the result before it shows it.</summary>
public sealed class RgbEffectBuilder(IRgbEffectFactory factory)
{
    private RgbEffect effectType;
    private TimeSpan? speed;
    private readonly List<Vector3> colors = [];
    private TimeSpan? fadeDuration;
    private TimeSpan? breathDuration;
    private float? minBrightness;
    private float? maxBrightness;

    public RgbEffectBuilder WithEffect(RgbEffect effectType) { this.effectType = effectType; return this; }
    public RgbEffectBuilder WithSpeed(int milliseconds) { speed = TimeSpan.FromMilliseconds(milliseconds); return this; }
    /// <summary>Adds the effect's next color, as #RRGGBB.</summary>
    public RgbEffectBuilder WithColor(string hexColor) { colors.Add(ColorHelper.HexToRgb(hexColor)); return this; }
    public RgbEffectBuilder WithFadeDuration(double seconds) { fadeDuration = TimeSpan.FromSeconds(seconds); return this; }
    public RgbEffectBuilder WithBreathDuration(double seconds) { breathDuration = TimeSpan.FromSeconds(seconds); return this; }
    /// <summary>Brightness from 0 to 1.</summary>
    public RgbEffectBuilder WithMinBrightness(double brightness) { minBrightness = (float)brightness; return this; }
    /// <summary>Brightness from 0 to 1.</summary>
    public RgbEffectBuilder WithMaxBrightness(double brightness) { maxBrightness = (float)brightness; return this; }

    public IRgbEffect Build()
    {
        var effect = factory.Create(effectType);
        if (speed is { } value) effect.Speed = value;
        for (var i = 0; i < colors.Count && i < effect.Colors.Count; i++) effect.SetColor(i, colors[i]);
        if (effect is FadeEffect fade && fadeDuration is { } fadeFor) fade.FadeDuration = fadeFor;
        if (effect is BreathingEffect breathing)
        {
            if (breathDuration is { } breathFor) breathing.BreathDuration = breathFor;
            if (minBrightness is { } minimum) breathing.MinBrightness = minimum;
            if (maxBrightness is { } maximum) breathing.MaxBrightness = maximum;
        }
        return effect;
    }
}
