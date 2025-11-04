namespace BobFx.Core.Services
{
    public class WLedOptions
    {
        public LedOptions Led { get; set; } = new LedOptions();
        public UpdOptions Udp { get; set; } = new UpdOptions();
        public DiscoveryOptions Discovery { get; set; } = new DiscoveryOptions();

        public class LedOptions
        {
            public int LedCount { get; set; } = 30;
        }

        public class UpdOptions
        {
            public string TargetAddress { get; set; } = "255.255.255.255";
            public int TargetPort { get; set; } = 21324;
            public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromMilliseconds(16);
        }

        public class DiscoveryOptions
        {
            public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(60);
        }
    }
}
