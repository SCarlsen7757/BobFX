using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Zeroconf;
namespace BobFx.Core.Services;

public record WLedDevice(IPAddress Address, int Port, string Name, string HostName, string Mac, string Version, DateTime LastSeen, bool IsOnline, Models.WLED.Info? Info);

public sealed class WLedDiscoveryService(ILogger<WLedDiscoveryService> logger, IOptions<WLedOptions> options, IHttpClientFactory httpFactory, IWLedDiscoverySource? source = null, InstallationSettingsStore? settings = null) : BackgroundService
{
    private string? lastError;
    private long lastScanUtcTicks;
    public string? LastError => Volatile.Read(ref lastError);
    public DateTimeOffset? LastScanUtc => Volatile.Read(ref lastScanUtcTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    private readonly IWLedDiscoverySource discoverySource = source ?? new MdnsWLedDiscoverySource();
    private readonly ConcurrentDictionary<string, WLedDevice> devices = new();
    private readonly Lock mutation = new();
    private long generation;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly TimeSpan interval = TimeSpan.FromSeconds(options.Value.Discovery.UpdateIntervalSeconds);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int MaxDevices = 256;
    private const int MaxResponseBytes = 64 * 1024;
    public event Action<IReadOnlyList<WLedDevice>>? DevicesUpdated;
    public IReadOnlyList<WLedDevice> Devices => devices.Values.OrderBy(d => d.Name).ThenBy(d => d.Address.ToString()).ToArray();
    public void Clear() { lock (mutation) { generation++; devices.Clear(); } Notify(); }
    public void Remove(IPAddress address)
    {
        lock (mutation)
        {
            generation++;
            foreach (var device in devices.Values.Where(d => d.Address.Equals(address))) devices.TryRemove(device.Mac, out _);
        }
        Notify();
    }
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!await scanGate.WaitAsync(0, cancellationToken)) return;
        var scanGeneration = Volatile.Read(ref generation);
        Volatile.Write(ref lastScanUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        try
        {
            IReadOnlyList<WLedAdvertisement> hosts;
            try { hosts = await discoverySource.ResolveAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                Volatile.Write(ref lastError, "mDNS resolution failed");
                if (settings is null || settings.Read().Devices.Count == 0) { AgeDevices(scanGeneration); Notify(); throw; }
                hosts = []; // Explicit registry addresses remain usable when mDNS fails.
            }
            if (settings is not null)
                hosts = hosts.Concat(settings.Snapshot.Devices.Select(d => new WLedAdvertisement(d.Address, d.HttpPort, d.Name))).DistinctBy(h => (h.Address, h.Port)).ToArray();
            using var http = httpFactory.CreateClient("wled");
            await Parallel.ForEachAsync(hosts.Take(MaxDevices), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (host, token) =>
                {
                    var ip = host.Address;
                    if (!IPAddress.TryParse(ip, out var address) || !IsLocalDeviceAddress(address)) return;
                    if (host.Port is < 1 or > 65535) return;
                    try
                    {
                        using var response = await http.GetAsync($"http://{address}:{host.Port}/json/info", HttpCompletionOption.ResponseHeadersRead, token);
                        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxResponseBytes) return;
                        // ResponseHeadersRead does not apply HttpClient.Timeout to body reads.
                        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                        await using var stream = await response.Content.ReadAsStreamAsync(readDeadline.Token);
                        using var buffer = new MemoryStream();
                        var chunk = new byte[4096];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, readDeadline.Token)) != 0)
                        {
                            if (buffer.Length + read > MaxResponseBytes) return;
                            buffer.Write(chunk, 0, read);
                        }
                        var info = JsonSerializer.Deserialize<Models.WLED.Info>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), JsonOptions);
                        if (info is null || info.Brand != "WLED" || !ValidMac(info.Mac)) return;
                        var key = info.Mac.Replace(":", "").ToLowerInvariant();
                        lock (mutation)
                        {
                            if (generation != scanGeneration || (!devices.ContainsKey(key) && devices.Count >= MaxDevices)) return;
                            devices[key] = new(address, host.Port, info.Name, host.HostName, key, info.Ver, DateTime.UtcNow, true, info);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
                    { logger.LogDebug(ex, "WLED enrichment failed for {Address}", address); }
                });
            AgeDevices(scanGeneration);
            if (settings is not null)
            {
                var online = Devices.Where(d => d.IsOnline && d.Info?.Leds?.Count is > 0 and <= 65535 && d.Info.Udpport is > 0 and <= 65535).ToDictionary(d => d.Mac);
                DeviceSettings Refresh(DeviceSettings saved, InstallationSettings s)
                {
                    if (!online.TryGetValue(saved.Mac, out var found)) return saved;
                    var count = found.Info!.Leds.Count;
                    // A hardware count shrink needs operator remapping; retain the last
                    // valid configuration while output reports/suppresses invalid mappings.
                    if (s.Boards.Any(b => b.DeviceMac == saved.Mac && b.Edges.Any(e => (long)e.Start + e.Count > count))) count = saved.LedCount;
                    return saved with { Address = found.Address.ToString(), HttpPort = found.Port, LedCount = count, UdpPort = found.Info.Udpport };
                }
                var snapshot = settings.Read();
                if (snapshot.Devices.Any(d => Refresh(d, snapshot) != d))
                    await settings.UpdateAsync(s => s with { Devices = s.Devices.Select(d => Refresh(d, s)).ToList() }, cancellationToken);
            }
            Volatile.Write(ref lastError, null);
            Notify();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Volatile.Write(ref lastError, "WLED discovery scan failed"); logger.LogWarning(ex, "WLED discovery failed"); throw; }
        finally { scanGate.Release(); }
    }
    private void AgeDevices(long scanGeneration)
    {
        var now = DateTime.UtcNow;
        lock (mutation)
        {
            if (generation != scanGeneration) return;
            foreach (var pair in devices.ToArray())
            {
                if (pair.Value.LastSeen < now - TimeSpan.FromDays(1)) devices.TryRemove(pair.Key, out _);
                else if (pair.Value.LastSeen < now - interval * 4) devices.TryUpdate(pair.Key, pair.Value with { IsOnline = false }, pair.Value);
            }
        }
    }
    internal static bool IsLocalDeviceAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            172 => bytes[1] is >= 16 and <= 31,
            192 => bytes[1] == 168,
            169 => bytes[1] == 254,
            _ => false
        };
    }
    private static bool ValidMac(string? mac)
    {
        var normalized = mac?.Replace(":", "");
        return normalized is { Length: 12 } && normalized.All(Uri.IsHexDigit);
    }
    private void Notify()
    {
        var snapshot = Devices;
        if (DevicesUpdated is null) return;
        foreach (Action<IReadOnlyList<WLedDevice>> subscriber in DevicesUpdated.GetInvocationList())
            try { subscriber(snapshot); } catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { logger.LogDebug(ex, "Discovery subscriber failed"); }
    }
    private async Task TryScanAsync(CancellationToken token)
    {
        try { await ScanAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
        { logger.LogDebug(ex, "Discovery scan will retry on the next interval"); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TryScanAsync(stoppingToken);
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await TryScanAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels discovery and its interval timer. */ }
    }
}

public sealed record WLedAdvertisement(string Address, int Port, string HostName);
public interface IWLedDiscoverySource
{
    Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken cancellationToken);
}
internal sealed class MdnsWLedDiscoverySource : IWLedDiscoverySource
{
    public async Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken cancellationToken)
    {
        const string serviceType = "_wled._tcp.local.";
        var hosts = await ZeroconfResolver.ResolveAsync(serviceType, scanTime: TimeSpan.FromSeconds(3), cancellationToken: cancellationToken);
        var result = new List<WLedAdvertisement>();
        foreach (var host in hosts.Take(256))
        {
            var address = host.IPAddresses.FirstOrDefault();
            var service = host.Services.Values.FirstOrDefault(s => s.Name == serviceType);
            if (address is not null && service is not null) result.Add(new(address, service.Port, host.DisplayName));
        }
        return result;
    }
}
