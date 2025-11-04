namespace BobFx.Core.Services.Effects;

/// <summary>
/// Metadata about what each effect needs.
/// </summary>
public class RgbEffectInfo
{
    public int RequiredColors { get; set; }
    public int OptionalColors { get; set; }
    public string Description { get; set; } = string.Empty;
    public TimeSpan DefaultSpeed { get; set; }
    public string[] ColorLabels { get; set; } = [];
    public Dictionary<string, Type> CustomParameters { get; set; } = [];

    public int TotalColors => RequiredColors + OptionalColors;
}

/// <summary>
/// Defines what parameters each effect needs and provides default configurations.
/// </summary>
public static class RgbEffectDefaults
{
    public static readonly Dictionary<RgbEffect, RgbEffectInfo> EffectInfo = new()
    {
        [RgbEffect.Off] = new RgbEffectInfo
        {
            RequiredColors = 0,
            OptionalColors = 0,
            Description = "Turn off all LEDs",
            DefaultSpeed = TimeSpan.FromMilliseconds(100)
        },
        [RgbEffect.Rainbow] = new RgbEffectInfo
        {
            RequiredColors = 0,
            OptionalColors = 0,
            Description = "Animated rainbow across all LEDs",
            DefaultSpeed = TimeSpan.FromMilliseconds(50)
        },
        [RgbEffect.Strobe] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Blink between one color and off",
            DefaultSpeed = TimeSpan.FromMilliseconds(250),
            ColorLabels = ["Blink Color"]
        },
        [RgbEffect.Scanner] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Single LED moving back and forth",
            DefaultSpeed = TimeSpan.FromMilliseconds(100),
            ColorLabels = ["Scanner Color"]
        },
        [RgbEffect.Solid] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Solid color on all LEDs",
            DefaultSpeed = TimeSpan.FromMilliseconds(100),
            ColorLabels = ["Color"]
        },
        [RgbEffect.FadeIn] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Gradually fade in to specified color over a set duration",
            DefaultSpeed = TimeSpan.FromMilliseconds(50),
            ColorLabels = ["Target Color"],
            CustomParameters = new Dictionary<string, Type> { ["FadeDuration"] = typeof(TimeSpan) }
        },
        [RgbEffect.FadeOut] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Gradually fade out from specified color over a set duration",
            DefaultSpeed = TimeSpan.FromMilliseconds(50),
            ColorLabels = ["Start Color"],
            CustomParameters = new Dictionary<string, Type> { ["FadeDuration"] = typeof(TimeSpan) }
        },
        [RgbEffect.Breathing] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Breathing effect that fades between configurable minimum and maximum brightness",
            DefaultSpeed = TimeSpan.FromMilliseconds(50),
            ColorLabels = ["Color"],
            CustomParameters = new Dictionary<string, Type> { ["BreathDuration"] = typeof(TimeSpan), ["MinBrightness"] = typeof(float), ["MaxBrightness"] = typeof(float) }
        },
        [RgbEffect.Twinkle] = new RgbEffectInfo
        {
            RequiredColors = 1,
            OptionalColors = 0,
            Description = "Random twinkling effect with LEDs fading in and out independently",
            DefaultSpeed = TimeSpan.FromMilliseconds(100),
            ColorLabels = ["Color"]
        },
        [RgbEffect.Blink] = new RgbEffectInfo
        {
            RequiredColors = 2,
            OptionalColors = 0,
            Description = "Alternates between two colors",
            DefaultSpeed = TimeSpan.FromMilliseconds(500),
            ColorLabels = ["Color 1", "Color 2"]
        }
    };
}