namespace BobFx.Core.Services.Effects;

/// <summary>
/// Factory for creating RGB effects.
/// </summary>
public interface IRgbEffectFactory
{
    /// <summary>
    /// Create an effect instance by type.
    /// </summary>
    IRgbEffect Create(RgbEffect effectType);

    /// <summary>
    /// Get all registered effect types.
    /// </summary>
    IEnumerable<RgbEffect> GetRegisteredEffects();
}

/// <summary>
/// Registry and factory for RGB effects.
/// </summary>
public class RgbEffectFactory : IRgbEffectFactory
{
    private readonly Dictionary<RgbEffect, Func<IRgbEffect>> _factories = [];

    public RgbEffectFactory()
    {
        // Register all built-in effects
        RegisterDefaults();
    }

    private void Register<T>() where T : IRgbEffect, new() => _factories[new T().EffectType] = () => new T();

    public IRgbEffect Create(RgbEffect effectType)
    {
        if (_factories.TryGetValue(effectType, out var factory))
        {
            var effect = factory();
            effect.Speed = effect.GetInfo().DefaultSpeed;
            return effect;
        }

        throw new InvalidOperationException($"No factory registered for effect type: {effectType}");
    }

    public IEnumerable<RgbEffect> GetRegisteredEffects()
    {
        return _factories.Keys;
    }

    private void RegisterDefaults()
    {
        Register<OffEffect>();
        Register<RainbowEffect>();
        Register<SolidEffect>();
        Register<StrobeEffect>();
        Register<ScannerEffect>();
        Register<FadeInEffect>();
        Register<FadeOutEffect>();
        Register<BreathingEffect>();
        Register<TwinkleEffect>();
        Register<BlinkEffect>();
    }
}
