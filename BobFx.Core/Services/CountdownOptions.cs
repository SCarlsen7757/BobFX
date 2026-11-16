namespace BobFx.Core.Services;
public sealed class CountdownOptions
{
    public int DurationSeconds { get; set; } = 720;
    public int DeviationSeconds { get; set; } = 120;
    public bool IsValid() => SettingLimits.Current.AllowsCountdown(DurationSeconds, DeviationSeconds);
}
