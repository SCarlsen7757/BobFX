namespace BobFx.Core.Services;

public sealed class WLedRegistryService(InstallationSettingsStore settings, GameCoordinator control, IHttpClientFactory clients)
{
    /// <summary>WLED's UDP port for realtime frames unless the device says otherwise.</summary>
    public const int DefaultUdpPort = 21324;
    /// <summary>A discovered device as it is registered. A device that did not report its LEDs gets none and is
    /// refused by validation until it does.</summary>
    public static DeviceSettings ToSettings(WLedDevice device) => new(device.Mac, device.Name, device.Address.ToString(), device.Port,
        device.Info?.Leds?.Count ?? 0, device.Info?.Udpport ?? DefaultUdpPort);
    public Task SelectAsync(WLedDevice device, CancellationToken token = default) => control.ConfigureAsync(() =>
        settings.UpdateAsync(s => s with { Devices = s.Devices.Where(d => d.Mac != device.Mac).Append(ToSettings(device)).ToList() }, token));
    /// <summary>Registers a controller that exists only in BobFX, with the LEDs of a standard board. It takes
    /// part in layout, calibration, tests and games like a real one, and the live view shows its frames.</summary>
    public async Task<DeviceSettings> AddSimulatedAsync(CancellationToken token = default)
    {
        DeviceSettings? added = null;
        await control.ConfigureAsync(() => settings.UpdateAsync(s =>
        {
            // A locally administered MAC range that no real WLED controller reports.
            static string Mac(int number) => $"02b0bf{number:x6}";
            var number = 1;
            while (s.Devices.Any(d => d.Mac == Mac(number))) number++;
            added = new(Mac(number), $"Simulated board {number}", "169.254.0.1", 80, BoardMapping.LedsPerBoard, DefaultUdpPort, Simulated: true);
            return s with { Devices = [.. s.Devices, added] };
        }, token));
        return added!;
    }
    public Task AddManualAsync(string address, int port, CancellationToken token = default) => control.ConfigureAsync(async () =>
    {
        if (!InstallationSettingsStore.LocalAddress(address) || port is < 1 or > 65535)
            throw new ArgumentException("Enter a private IPv4 address and valid HTTP port.");
        using var client = clients.CreateClient("wled");
        Models.WLED.Info? info;
        try { info = await WLedInfo.ReadAsync(client, address, port, token); }
        catch (InvalidDataException ex) { throw new ArgumentException(ex.Message); }
        if (info?.Leds is null || !WLedInfo.TryIdentify(info, out var mac)) throw new ArgumentException("This endpoint did not identify as WLED.");
        var device = new DeviceSettings(mac, info.Name ?? address, address, port, info.Leds.Count, info.Udpport);
        await settings.UpdateAsync(s => s with { Devices = s.Devices.Where(d => d.Mac != mac).Append(device).ToList() }, token);
    });
}
