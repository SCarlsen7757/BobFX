using System.Net;
namespace BobFx.Core.Services;
public sealed class WLedOptions
{
    public LedOptions Led { get; set; } = new();
    public UdpOptions Udp { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
    public bool IsValid() => Led.LedCount is >= 1 and <= 490 &&
        IPAddress.TryParse(Udp.TargetAddress, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        Udp.TargetPort is >= 1 and <= 65535 && Udp.UpdateIntervalMs is >= 10 and <= 500 &&
        Discovery.UpdateIntervalSeconds is >= 5 and <= 3600;
    public sealed class LedOptions { public int LedCount { get; set; } = 30; }
    public sealed class UdpOptions
    {
        public string TargetAddress { get; set; } = "255.255.255.255";
        public int TargetPort { get; set; } = 21324;
        public int UpdateIntervalMs { get; set; } = 16;
    }
    public sealed class DiscoveryOptions { public int UpdateIntervalSeconds { get; set; } = 60; }
}
