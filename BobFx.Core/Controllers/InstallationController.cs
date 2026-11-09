using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BobFx.Core.Controllers;

[ApiController]
[Route("api/installation")]
public sealed class InstallationController(InstallationSettingsStore settings, RgbAndSoundControlService control,
    InstallationOutputService output, WLedRegistryService registry, ControllerCredentials credentials,
    WLedDiscoveryService discovery, BusyBarService busyBars) : ControllerBase
{
    [HttpGet]
    public InstallationSettings Get() => settings.Snapshot;
    [HttpPut]
    public Task<IActionResult> Save(InstallationSettings value, CancellationToken token) => Execute(async () =>
        await control.ConfigureAsync(() => settings.SaveAsync(value, token)));
    [HttpPut("layout")]
    public Task<IActionResult> SaveLayout(LayoutRequest value, CancellationToken token) => Execute(() => control.ConfigureAsync(async () =>
    {
        await settings.UpdateAsync(s => s with { Boards = value.Boards, IdleSync = value.IdleSync, Animation = value.Animation }, token);
        await output.ApplyIdleSyncAsync(token);
    }));
    [HttpPut("controllers")]
    public Task<IActionResult> SaveControllers(ControllersRequest value, CancellationToken token) => Execute(() => control.ConfigureAsync(async () =>
    {
        if (value.Controllers is null || value.Passwords is null) throw new ArgumentException("Controllers and passwords are required.");
        InstallationSettingsStore.Validate(settings.Snapshot with { Controllers = value.Controllers });
        foreach (var (id, password) in value.Passwords)
        {
            if (!value.Controllers.Any(c => c.Id == id)) throw new ArgumentException("Save the controller before setting its password.");
            ControllerCredentials.ValidatePassword(password);
        }
        await settings.UpdateAsync(s => s with { Controllers = value.Controllers }, token);
        foreach (var (id, password) in value.Passwords) await credentials.SetAsync(id, password, token);
    }));
    [HttpPost("controllers/test")]
    public async Task<IActionResult> TestController(ControllerTestRequest value, CancellationToken token)
    {
        try { return Ok(new { apiVersion = await busyBars.TestConnectionAsync(value.Controller, value.Password, token) }); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return Problem("Connection failed. Check address, local API access and device password.", statusCode: 502); }
    }
    [HttpPost("devices/{mac}/select")]
    public Task<IActionResult> SelectDevice(string mac, CancellationToken token) => Execute(async () =>
    {
        var device = discovery.Devices.FirstOrDefault(d => d.Mac == mac) ?? throw new ArgumentException("Device is no longer discovered. Scan again.");
        await registry.SelectAsync(device, token);
    });
    [HttpPut("devices/{mac}")]
    public Task<IActionResult> SelectSaved(string mac, DeviceSelectionRequest value, CancellationToken token) => Execute(() => control.ConfigureAsync(() =>
        settings.UpdateAsync(s => s with { Devices = s.Devices.Select(d => d.Mac == mac ? d with { Selected = value.Selected } : d).ToList() }, token)));
    [HttpDelete("devices/{mac}")]
    public Task<IActionResult> RemoveDevice(string mac, CancellationToken token) => Execute(() => control.ConfigureAsync(() => settings.UpdateAsync(s =>
    {
        if (s.Boards.Any(b => b.DeviceMac == mac) || s.IdleSync.MasterMac == mac || s.IdleSync.Followers.Contains(mac))
            throw new ArgumentException("Remove this device from board layout and idle sync before deleting it.");
        return s with { Devices = s.Devices.Where(d => d.Mac != mac).ToList() };
    }, token)));
    [HttpPost("devices/{mac}/locate-discovered")]
    public Task<IActionResult> LocateDiscovered(string mac, CancellationToken token) => Execute(async () =>
    {
        var d = discovery.Devices.FirstOrDefault(d => d.Mac == mac) ?? throw new ArgumentException("Device is no longer discovered. Scan again.");
        await output.LocateDeviceAsync(new DeviceSettings(d.Mac, d.Name, d.Address.ToString(), d.Port, d.Info?.Leds?.Count ?? 0, d.Info?.Udpport ?? 21324, false), token);
    });
    [HttpPost("devices/manual")]
    public Task<IActionResult> AddManual(ManualDeviceRequest request, CancellationToken token) => Execute(() => registry.AddManualAsync(request.Address, request.Port, token));
    [HttpPost("devices/{mac}/locate")]
    public Task<IActionResult> Locate(string mac, CancellationToken token) => Execute(() => output.LocateAsync(mac, token));
    [HttpPost("idle-sync/apply")]
    public Task<IActionResult> Sync(CancellationToken token) => Execute(() => control.ConfigureAsync(() => output.ApplyIdleSyncAsync(token)));
    [HttpGet("controllers/{id}/password")]
    public IActionResult PasswordStatus(string id) => settings.Snapshot.Controllers.Any(c => c.Id == id)
        ? Ok(new { hasPassword = credentials.HasPassword(id) }) : NotFound();
    [HttpPut("controllers/{id}/password")]
    public Task<IActionResult> SetPassword(string id, ControllerPasswordRequest request, CancellationToken token) => Execute(() => control.ConfigureAsync(async () =>
    {
        if (!settings.Snapshot.Controllers.Any(c => c.Id == id)) throw new ArgumentException("Save the controller before setting its password.");
        await credentials.SetAsync(id, request.Password, token);
    }));
    private async Task<IActionResult> Execute(Func<Task> action)
    {
        try { await action(); return Ok(); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
        catch (JsonException) { return Problem("Device returned invalid JSON.", statusCode: 400); }
        catch (InvalidOperationException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (HttpRequestException) { return Problem("Device could not be reached.", statusCode: 502); }
        catch (OperationCanceledException) { return Problem("Operation timed out or was cancelled.", statusCode: 504); }
        catch (IOException) { return Problem("Settings could not be saved; check data-volume permissions.", statusCode: 503); }
    }
}
public sealed record ManualDeviceRequest(string Address, int Port = 80);
public sealed record ControllerPasswordRequest(string Password);
public sealed record LayoutRequest(List<BoardSettings> Boards, IdleSyncSettings IdleSync, AnimationSettings Animation);
public sealed record ControllersRequest(List<BusyBarSettings> Controllers, Dictionary<string, string> Passwords);
public sealed record ControllerTestRequest(BusyBarSettings Controller, string? Password = null);
public sealed record DeviceSelectionRequest(bool Selected);
