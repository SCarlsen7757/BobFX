using System.Text.Json;

namespace BobFx.Core.Services;

public sealed class WLedRegistryService(InstallationSettingsStore settings, RgbAndSoundControlService control, IHttpClientFactory clients)
{
    public Task SelectAsync(WLedDevice device, CancellationToken token = default) => control.ConfigureAsync(() =>
        settings.UpdateAsync(s => s with { Devices = s.Devices.Where(d => d.Mac != device.Mac).Append(new DeviceSettings(device.Mac,
            device.Name, device.Address.ToString(), device.Port, device.Info?.Leds?.Count ?? 0, device.Info?.Udpport ?? 21324)).ToList() }, token));
    public Task AddManualAsync(string address, int port, CancellationToken token = default) => control.ConfigureAsync(async () =>
    {
        if (!InstallationSettingsStore.LocalAddress(address) || port is < 1 or > 65535)
            throw new ArgumentException("Enter a private IPv4 address and valid HTTP port.");
        using var client = clients.CreateClient("wled");
        using var response = await client.GetAsync($"http://{address}:{port}/json/info", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + read > 64 * 1024) throw new ArgumentException("Device response is too large.");
            buffer.Write(chunk, 0, read);
        }
        var info = JsonSerializer.Deserialize<Models.WLED.Info>(buffer.ToArray(), InstallationSettingsStore.JsonOptions);
        var mac = info?.Mac?.Replace(":", "").ToLowerInvariant() ?? "";
        if (info?.Brand != "WLED" || !InstallationSettingsStore.ValidMac(mac) || info.Leds is null)
            throw new ArgumentException("This endpoint did not identify as WLED.");
        var device = new DeviceSettings(mac, info.Name, address, port, info.Leds.Count, info.Udpport);
        await settings.UpdateAsync(s => s with { Devices = s.Devices.Where(d => d.Mac != mac).Append(device).ToList() }, token);
    });
}
