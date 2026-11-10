namespace BobFx.Core.Services;
public sealed class CountdownOptions
{
    public int DurationSeconds { get; set; } = 600;
    public int DeviationSeconds { get; set; } = 120;
    public bool IsValid() => DurationSeconds is >= 1 and <= 86400 && DeviationSeconds >= 0 && DeviationSeconds < DurationSeconds;
}
