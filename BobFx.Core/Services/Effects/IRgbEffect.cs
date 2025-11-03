using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>
/// Interface for all RGB effects.
/// </summary>
public interface IRgbEffect
{
    /// <summary>
    /// The type of effect this represents.
    /// </summary>
    RgbEffect EffectType { get; }

    /// <summary>
    /// The speed/delay between effect updates.
    /// </summary>
    TimeSpan Speed { get; set; }

    /// <summary>
    /// Initialize the effect before first render.
    /// </summary>
    void Initialize(int ledCount);

    /// <summary>
    /// Apply the effect to the LED array.
    /// </summary>
    /// <param name="leds">The LED array to update.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True if the effect has completed and should stop, false to continue.</returns>
    bool Apply(Span<Vector3> leds, CancellationToken token);

    /// <summary>
    /// Reset the effect to its initial state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Get metadata about this effect.
    /// </summary>
    RgbEffectInfo GetInfo();

    /// <summary>
    /// Validate that the effect is properly configured.
    /// </summary>
    ValidationResult Validate();

    /// <summary>
    /// Clone the effect with the same configuration.
    /// </summary>
    IRgbEffect Clone();
}
