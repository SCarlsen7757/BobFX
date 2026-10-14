using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BobFx.Core.Controllers;

[ApiController]
[Route("api/installation")]
public sealed class InstallationController(InstallationSettingsStore settings, RgbAndSoundControlService control,
    InstallationOutputService output, WLedRegistryService registry) : ControllerBase
{
    [HttpGet]
    public InstallationSettings Get() => settings.Snapshot;
    [HttpPut]
    public Task<IActionResult> Save(InstallationSettings value, CancellationToken token) => Execute(async () =>
        await control.ConfigureAsync(() => settings.SaveAsync(value, token)));
    [HttpPost("devices/manual")]
    public Task<IActionResult> AddManual(ManualDeviceRequest request, CancellationToken token) => Execute(() => registry.AddManualAsync(request.Address, request.Port, token));
    [HttpPost("devices/{mac}/locate")]
    public Task<IActionResult> Locate(string mac, CancellationToken token) => Execute(() => output.LocateAsync(mac, token));
    [HttpPost("idle-sync/apply")]
    public Task<IActionResult> Sync(CancellationToken token) => Execute(() => control.ConfigureAsync(() => output.ApplyIdleSyncAsync(token)));
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
