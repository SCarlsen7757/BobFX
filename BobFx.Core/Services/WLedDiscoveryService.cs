using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Zeroconf;

namespace BobFx.Core.Services
{
    public record WLedDevice(IPAddress Address, int Port, string Name, string HostName, string Mac, string Version, DateTime LastSeen, bool IsOnline, Models.WLED.Info? Info);

    /// <summary>
    /// Background service that periodically discovers WLED devices via mDNS and exposes the
    /// discovery results to other components. Acts as both a singleton discovery store
    /// and a hosted background poller.
    /// </summary>
    public class WLedDiscoveryService : BackgroundService
    {
        private readonly ILogger<WLedDiscoveryService> logger;
        private readonly IHttpClientFactory httpFactory;
        private readonly ConcurrentDictionary<string, WLedDevice> devices = [];
        private readonly TimeSpan interval;

        public event Action<IReadOnlyList<WLedDevice>>? DevicesUpdated;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public WLedDiscoveryService(ILogger<WLedDiscoveryService> logger,
                                              IOptionsMonitor<WLedOptions.DiscoveryOptions> discoveryOptions,
                                              IHttpClientFactory httpFactory)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            ArgumentNullException.ThrowIfNull(discoveryOptions);

            interval = discoveryOptions.CurrentValue.UpdateInterval;
        }

        public IReadOnlyList<WLedDevice> Devices => devices.Values
                .OrderBy(d => d.Name)
                .ThenBy(d => d.Address.ToString())
                .ToList()
                .AsReadOnly();

        public void Clear()
        {
            devices.Clear();
            DevicesUpdated?.Invoke(Devices);
        }

        public void Remove(IPAddress address)
        {
            var device = devices.Values.FirstOrDefault(d => d.Address.Equals(address));
            if (device != null && devices.TryRemove(device.Mac, out _))
            {
                DevicesUpdated?.Invoke(Devices);
            }
        }

        /// <summary>
        /// Public scan method that triggers an mDNS lookup for WLED devices.
        /// Can be called manually (e.g. from the UI) in addition to the periodic background scans.
        /// </summary>
        public async Task ScanAsync(CancellationToken cancellationToken = default)
        {
            var updated = new List<WLedDevice>();

            try
            {
                const string serviceType = "_wled._tcp.local.";
                logger.LogInformation("Starting mDNS scan for WLED devices ({Service})", serviceType);

                IReadOnlyList<IZeroconfHost> hosts;
                try
                {
                    hosts = await ZeroconfResolver.ResolveAsync(serviceType, scanTime: TimeSpan.FromSeconds(3), cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "mDNS resolution failed");
                    hosts = [];
                }

                var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(1);

                var now = DateTime.UtcNow;

                foreach (var host in hosts)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var ip = host.IPAddress ?? host.IPAddresses[0];
                    if (string.IsNullOrWhiteSpace(ip))
                        continue;

                    var service = host.Services.Values.FirstOrDefault();
                    var port = service?.Port ?? 80;

                    string hostName = host.DisplayName;
                    string name = "WLED";
                    string mac = string.Empty;
                    string ver = string.Empty;

                    Models.WLED.Info? info = null;

                    // Try to enrich from /json/info (more accurate name/version/mac)
                    try
                    {
                        var url = $"http://{ip}:{port}/json/info";
                        using var resp = await http.GetAsync(url, cancellationToken);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                            var fetchedInfo = JsonSerializer.Deserialize<Models.WLED.Info>(json, JsonOptions);
                            if (fetchedInfo != null && (fetchedInfo.Brand == "WLED" || !string.IsNullOrEmpty(fetchedInfo.Ver)))
                            {
                                name = fetchedInfo.Name;
                                ver = fetchedInfo.Ver;
                                mac = fetchedInfo.Mac;
                                info = fetchedInfo;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Enrichment /json/info failed for {Host}", ip);
                    }

                    if (string.IsNullOrWhiteSpace(mac))
                        continue;

                    if (!IPAddress.TryParse(ip, out var ipAddr))
                        continue;

                    var key = mac;
                    WLedDevice device;
                    if (devices.TryGetValue(key, out var existing))
                    {
                        device = existing with { LastSeen = now, IsOnline = true, Name = name, HostName = hostName, Version = ver, Info = info ?? existing.Info };
                        devices[key] = device;
                    }
                    else
                    {
                        device = new WLedDevice(ipAddr, port, name, hostName, mac, ver, now, true, info);
                        devices[key] = device;
                    }
                    updated.Add(device);
                    logger.LogInformation("Found WLED device {Name} at {Ip}:{Port}", name, ip, port);
                }

                var cutoff = DateTime.UtcNow - (interval * 4);
                foreach (var kvp in devices.ToArray())
                {
                    if (kvp.Value.LastSeen < cutoff)
                    {
                        devices[kvp.Key] = kvp.Value with { IsOnline = false };
                    }
                }

                DevicesUpdated?.Invoke(Devices);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "mDNS scan encountered an error");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("WLED discovery background service starting. Interval: {Interval}s", interval.TotalSeconds);

            try
            {
                // Run an initial scan immediately
                try
                {
                    await ScanAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Initial WLED discovery scan failed");
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                        await ScanAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // normal shutdown
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "WLED discovery scan failed during background execution");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected when stopping
            }
            finally
            {
                logger.LogInformation("WLED discovery background service stopping");
            }
        }
    }
}
