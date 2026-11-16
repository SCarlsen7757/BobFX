namespace BobFx.Core.Services.Effects;

public interface IRgbEffectFactory
{
    /// <summary>A new effect of this type at its default speed.</summary>
    IRgbEffect Create(RgbEffect effectType);
    RgbEffectInfo Info(RgbEffect effectType);
    IEnumerable<RgbEffect> GetRegisteredEffects();
}

/// <summary>The effects there are. A new effect class is added here and nowhere else.</summary>
public sealed class RgbEffectFactory : IRgbEffectFactory
{
    private static readonly Dictionary<RgbEffect, Func<IRgbEffect>> Effects = new()
    {
        [RgbEffect.Off] = () => new OffEffect(),
        [RgbEffect.Rainbow] = () => new RainbowEffect(),
        [RgbEffect.Solid] = () => new SolidEffect(),
        [RgbEffect.Strobe] = () => new StrobeEffect(),
        [RgbEffect.Scanner] = () => new ScannerEffect(),
        [RgbEffect.FadeIn] = () => new FadeInEffect(),
        [RgbEffect.FadeOut] = () => new FadeOutEffect(),
        [RgbEffect.Breathing] = () => new BreathingEffect(),
        [RgbEffect.Twinkle] = () => new TwinkleEffect(),
        [RgbEffect.Blink] = () => new BlinkEffect(),
    };
    private static readonly Dictionary<RgbEffect, RgbEffectInfo> Infos = Effects.ToDictionary(entry => entry.Key, entry => entry.Value().GetInfo());

    public IRgbEffect Create(RgbEffect effectType)
    {
        if (!Effects.TryGetValue(effectType, out var create)) throw new InvalidOperationException($"No effect of type {effectType}.");
        var effect = create();
        effect.Speed = effect.GetInfo().DefaultSpeed;
        return effect;
    }

    public RgbEffectInfo Info(RgbEffect effectType) => Infos[effectType];

    public IEnumerable<RgbEffect> GetRegisteredEffects() => Effects.Keys;
}
