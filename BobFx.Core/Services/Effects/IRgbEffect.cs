using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>An animation of the virtual LED strip that manual effects are drawn on.</summary>
public interface IRgbEffect
{
    RgbEffect EffectType { get; }

    /// <summary>The time between two frames.</summary>
    TimeSpan Speed { get; set; }

    /// <summary>The effect's colors, in the order of its info's labels.</summary>
    IReadOnlyList<Vector3> Colors { get; }
    void SetColor(int index, Vector3 color);

    /// <summary>Prepares the effect for a strip and starts it from its beginning. The clock is the renderer's,
    /// so timed effects follow it.</summary>
    void Initialize(int ledCount, TimeProvider clock);

    /// <summary>Draws the next frame.</summary>
    /// <returns>True once the effect has finished and has nothing more to draw.</returns>
    bool Apply(Span<Vector3> leds);

    RgbEffectInfo GetInfo();

    /// <summary>Throws an <see cref="ArgumentException"/> that says what is wrong with the effect's settings.</summary>
    void EnsureValid();

    /// <summary>A copy with the same settings, which can be changed and tried without touching this one.</summary>
    IRgbEffect Clone();
}
