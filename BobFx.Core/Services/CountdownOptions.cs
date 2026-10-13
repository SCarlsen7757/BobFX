namespace BobFx.Core.Services;
public sealed class CountdownOptions
{
    public int PreDurationSeconds { get; set; } = 5;
    public int DurationSeconds { get; set; } = 600;
    public int DeviationSeconds { get; set; } = 120;
    public bool IsValid() => PreDurationSeconds is >= 0 and <= 60 && DurationSeconds is >= 1 and <= 86400 && DeviationSeconds >= 0 && DeviationSeconds < DurationSeconds;
}
