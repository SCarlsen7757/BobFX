namespace BobFx.Core.Services;
public sealed class WLedOptions
{
    public LedOptions Led { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
    public bool IsValid() => Led.LedCount is >= 1 and <= 490 && Discovery.UpdateIntervalSeconds is >= 5 and <= 3600;
    public sealed class LedOptions { public int LedCount { get; set; } = 30; }
    public sealed class DiscoveryOptions { public int UpdateIntervalSeconds { get; set; } = 30; }
}
