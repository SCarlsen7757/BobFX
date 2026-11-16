using BobFx.Core.Services.Effects;

namespace BobFx.Core.Services;

public readonly record struct NumberRange(double Min, double Max)
{
    public bool Contains(double value) => double.IsFinite(value) && value >= Min && value <= Max;
    public override string ToString() => $"{Min}–{Max}";
}

/// <summary>The ranges that settings and commands accept. Validation checks against them and the browser is sent
/// them with the UI state, so a form refuses exactly what the server would refuse.</summary>
public sealed record SettingLimits
{
    public static readonly SettingLimits Current = new();

    public NumberRange EffectSpeedMs { get; } = new(RgbEffectBase.MinSpeedMilliseconds, RgbEffectBase.MaxSpeedMilliseconds);
    /// <summary>A game lasts up to a day. Its random deviation is at least 0 and less than its duration.</summary>
    public NumberRange CountdownSeconds { get; } = new(1, 86400);
    public NumberRange BrightnessPercent { get; } = new(20, 100);
    public NumberRange DriftSpeedPercent { get; } = new(25, 200);
    public NumberRange FramesPerSecond { get; } = new(10, 60);
    public NumberRange FlareSeconds { get; } = new(1, 10);
    public NumberRange CometSeconds { get; } = new(1, 30);
    public NumberRange CometLapsPerSecond { get; } = new(1, 20);
    public NumberRange SyncGroup { get; } = new(1, 8);
    /// <summary>WLED addresses LEDs with 16 bits.</summary>
    public int MaxLeds { get; } = 65535;

    /// <summary>Whether a game of this length with this deviation can be played.</summary>
    public bool AllowsCountdown(int durationSeconds, int deviationSeconds) =>
        CountdownSeconds.Contains(durationSeconds) && deviationSeconds >= 0 && deviationSeconds < durationSeconds;
}
