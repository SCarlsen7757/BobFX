using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BobFx.Core.Controllers;

/// <summary>Setting up the installation. What a command refuses is answered by <see cref="ApiExceptionFilter"/>.</summary>
[ApiController]
[Route("api/installation")]
public sealed class InstallationController(InstallationSettingsStore settings, GameCoordinator control,
    InstallationOutputService output, WLedRegistryService registry, ControllerCredentials credentials,
    WLedDiscoveryService discovery, BusyBarService busyBars) : ControllerBase
{
    [HttpGet]
    public InstallationSettings Get() => settings.Snapshot;
    [HttpPut]
    public Task Save(InstallationSettings value, CancellationToken token) => control.ConfigureAsync(() => settings.SaveAsync(value, token));
    [HttpPut("layout")]
    public Task SaveLayout(LayoutRequest value, CancellationToken token) => control.ConfigureAsync(async () =>
    {
        await settings.UpdateAsync(s => s with { Boards = value.Boards, IdleSync = value.IdleSync }, token);
        await output.ApplyIdleSyncAsync(token);
    });
    [HttpPut("light-show")]
    public Task SaveLightShow(LightShowRequest value, CancellationToken token) => control.ConfigureAsync(() =>
        settings.UpdateAsync(s => s with { LightShow = value.LightShow, Animation = value.Animation }, token));
    /// <summary>Takes effect from the next game, so it is allowed while a game runs.</summary>
    [HttpPut("countdown")]
    public Task SaveCountdown(CountdownSettings value, CancellationToken token) => settings.UpdateAsync(s => s with { Countdown = value }, token);

    [HttpGet("light-show/themes")]
    public IReadOnlyList<LightShowTheme> LightShowThemes() => BoardLightShow.Themes;
    [HttpPost("light-show/test")]
    public async Task<LightShowTestView?> StartLightShowTest(LightShowTestRequest request)
    {
        await output.StartLightShowTestAsync(request);
        return output.LightShowTest;
    }
    /// <summary>Renders a part of the show for the browser preview. It only reads settings and owns no lights,
    /// so it works during games and tests too.</summary>
    [HttpPost("light-show/preview")]
    public LightShowPreview PreviewLightShow(LightShowTestRequest request) => LightShowPreviewRenderer.Render(settings.Snapshot, request);
    [HttpDelete("light-show/test")]
    public Task StopLightShowTest() => output.StopLightShowTestAsync();

    [HttpPut("controllers")]
    public Task SaveControllers(ControllersRequest value, CancellationToken token) => control.ConfigureAsync(async () =>
    {
        if (value.Controllers is null || value.Passwords is null) throw new ArgumentException("Controllers and passwords are required.");
        // Everything is checked before anything is saved, so a refused password does not leave the list half saved.
        InstallationSettingsStore.Validate(settings.Snapshot with { Controllers = value.Controllers });
        foreach (var (id, password) in value.Passwords)
        {
            if (!value.Controllers.Any(c => c.Id == id)) throw new ArgumentException("Save the controller before setting its password.");
            ControllerCredentials.ValidatePassword(password);
        }
        await settings.UpdateAsync(s => s with { Controllers = value.Controllers }, token);
        foreach (var (id, password) in value.Passwords) await credentials.SetAsync(id, password, token);
    });
    [HttpPost("controllers/test")]
    public async Task<IActionResult> TestController(ControllerTestRequest value, CancellationToken token)
    {
        // Whatever goes wrong on the way to the device, the operator checks the same three things.
        try { return Ok(new { apiVersion = await busyBars.TestConnectionAsync(value.Controller, value.Password, token) }); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return Problem("Connection failed. Check address, local API access and device password.", statusCode: 502); }
    }
    [HttpGet("controllers/status")]
    public IReadOnlyDictionary<string, BusyBarStatus> ControllerStatus() => busyBars.Statuses;
    [HttpGet("controllers/{id}/password")]
    public IActionResult PasswordStatus(string id) => settings.Snapshot.Controllers.Any(c => c.Id == id)
        ? Ok(new { hasPassword = credentials.HasPassword(id) }) : NotFound();
    [HttpPut("controllers/{id}/password")]
    public Task SetPassword(string id, ControllerPasswordRequest request, CancellationToken token) => control.ConfigureAsync(async () =>
    {
        if (!settings.Snapshot.Controllers.Any(c => c.Id == id)) throw new ArgumentException("Save the controller before setting its password.");
        await credentials.SetAsync(id, request.Password, token);
    });

    [HttpPost("devices/{mac}/select")]
    public Task SelectDevice(string mac, CancellationToken token) => registry.SelectAsync(Discovered(mac), token);
    [HttpPut("devices/{mac}")]
    public Task SelectSaved(string mac, DeviceSelectionRequest value, CancellationToken token) => control.ConfigureAsync(() =>
        settings.UpdateAsync(s => s with { Devices = s.Devices.Select(d => d.Mac == mac ? d with { Selected = value.Selected } : d).ToList() }, token));
    [HttpDelete("devices/{mac}")]
    public Task RemoveDevice(string mac, CancellationToken token) => control.ConfigureAsync(() => settings.UpdateAsync(s =>
    {
        if (s.Boards.Any(b => b.DeviceMac == mac) || s.IdleSync.MasterMac == mac || s.IdleSync.Followers.Contains(mac))
            throw new ArgumentException("Remove this device from board layout and idle sync before deleting it.");
        return s with { Devices = s.Devices.Where(d => d.Mac != mac).ToList() };
    }, token));
    [HttpPost("devices/{mac}/locate-discovered")]
    public Task LocateDiscovered(string mac, CancellationToken token) =>
        output.LocateDeviceAsync(WLedRegistryService.ToSettings(Discovered(mac)) with { Selected = false }, token);
    [HttpPost("devices/manual")]
    public Task AddManual(ManualDeviceRequest request, CancellationToken token) => registry.AddManualAsync(request.Address, request.Port, token);
    /// <summary>Adds a simulated controller and returns it, so a page can assign it to a board at once.</summary>
    [HttpPost("devices/simulated")]
    public Task<DeviceSettings> AddSimulated(CancellationToken token) => registry.AddSimulatedAsync(token);
    [HttpPost("devices/{mac}/locate")]
    public Task Locate(string mac, CancellationToken token) => output.LocateAsync(mac, token);
    private WLedDevice Discovered(string mac) => discovery.Find(mac) ?? throw new ArgumentException("Device is no longer discovered. Scan again.");

    [HttpPost("calibration")]
    public async Task<CalibrationView?> StartCalibration(CalibrationRequest request)
    {
        await output.StartCalibrationAsync(request);
        return output.Calibration;
    }
    [HttpPut("calibration/{id}")]
    public Task UpdateCalibration(string id, CalibrationRequest request) => output.UpdateCalibrationAsync(id, request);
    [HttpDelete("calibration/{id}")]
    public Task StopCalibration(string id) => output.StopCalibrationAsync(id);
    [HttpPost("idle-sync/apply")]
    public Task Sync(CancellationToken token) => control.ConfigureAsync(() => output.ApplyIdleSyncAsync(token));
    /// <summary>When a frame last went out, and what went wrong per controller.</summary>
    [HttpGet("status")]
    public object Status() => new { output.LastSendUtc, output.Errors };
}
public sealed record ManualDeviceRequest(string Address, int Port = 80);
public sealed record ControllerPasswordRequest(string Password);
public sealed record LayoutRequest(List<BoardSettings> Boards, IdleSyncSettings IdleSync);
public sealed record LightShowRequest(LightShowSettings LightShow, AnimationSettings Animation);
public sealed record ControllersRequest(List<BusyBarSettings> Controllers, Dictionary<string, string> Passwords);
public sealed record ControllerTestRequest(BusyBarSettings Controller, string? Password = null);
public sealed record DeviceSelectionRequest(bool Selected);
