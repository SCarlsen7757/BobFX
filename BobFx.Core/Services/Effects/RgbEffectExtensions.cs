namespace BobFx.Core.Services.Effects;

/// <summary>
/// Extension methods for creating common RGB effect configurations.
/// </summary>
public static class RgbEffectExtensions
{
    private static readonly IRgbEffectFactory _factory = new RgbEffectFactory();

    /// <summary>
    /// Create a fade in effect with specified color and duration.
    /// </summary>
    public static IRgbEffect CreateFadeIn(string color, TimeSpan duration, TimeSpan? speed = null)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.FadeIn)
            .WithColor(color)
            .WithFadeDuration(duration)
            .WithSpeed(speed ?? TimeSpan.FromMilliseconds(50))
            .Build();
    }

    /// <summary>
    /// Create a fade in effect with specified color and duration in seconds.
    /// </summary>
    public static IRgbEffect CreateFadeIn(string color, double durationSeconds, TimeSpan? speed = null)
    {
        return CreateFadeIn(color, TimeSpan.FromSeconds(durationSeconds), speed);
    }

    /// <summary>
    /// Create a fade out effect with specified color and duration.
    /// </summary>
    public static IRgbEffect CreateFadeOut(string color, TimeSpan duration, TimeSpan? speed = null)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.FadeOut)
            .WithColor(color)
            .WithFadeDuration(duration)
            .WithSpeed(speed ?? TimeSpan.FromMilliseconds(50))
            .Build();
    }

    /// <summary>
    /// Create a fade out effect with specified color and duration in seconds.
    /// </summary>
    public static IRgbEffect CreateFadeOut(string color, double durationSeconds, TimeSpan? speed = null)
    {
        return CreateFadeOut(color, TimeSpan.FromSeconds(durationSeconds), speed);
    }

    /// <summary>
    /// Create a strobe effect with two colors and custom speed.
    /// </summary>
    public static IRgbEffect CreateStrobe(string color1, string color2, TimeSpan speed)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.Strobe)
            .WithColor(color1)
            .WithColor(color2)
            .WithSpeed(speed)
            .Build();
    }

    /// <summary>
    /// Create a solid color effect.
    /// </summary>
    public static IRgbEffect CreateSolid(string color)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.Solid)
            .WithColor(color)
            .WithSpeed(TimeSpan.FromMilliseconds(100))
            .Build();
    }

    /// <summary>
    /// Create a rainbow effect.
    /// </summary>
    public static IRgbEffect CreateRainbow(TimeSpan? speed = null)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.Rainbow)
            .WithSpeed(speed ?? TimeSpan.FromMilliseconds(50))
            .Build();
    }

    /// <summary>
    /// Create a scanner effect.
    /// </summary>
    public static IRgbEffect CreateScanner(string color, TimeSpan? speed = null)
    {
        return new RgbEffectBuilder(_factory)
            .WithEffect(RgbEffect.Scanner)
            .WithColor(color)
            .WithSpeed(speed ?? TimeSpan.FromMilliseconds(100))
            .Build();
    }
}