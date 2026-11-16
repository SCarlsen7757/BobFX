using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BobFx.Core.Controllers;

[ApiController]
[Route("api/wled")]
public sealed class WLedController(WLedDiscoveryService discovery) : ControllerBase
{
    [HttpGet("devices")]
    public IEnumerable<DeviceView> Devices() => discovery.Devices.Select(UiStateService.ToView);

    [HttpGet("devices/{mac}")]
    public IActionResult Device(string mac)
    {
        if (discovery.Find(mac) is not { } device) return NotFound();
        var info = device.Info;
        return Ok(new { device = UiStateService.ToView(device), details = info is null ? null : new
        {
            wifiStrength = info.Wifi?.Rssi, ledCount = info.Leds?.Count, info.Brand, info.Product,
            architecture = info.Arch, coreVersion = info.Core, freeHeap = info.Freeheap,
            uptimeSeconds = info.Uptime, dataSourceType = info.Lm, dataSourceAddress = info.Lip
        }});
    }

    [HttpPost("scan")]
    public IActionResult Scan() { discovery.RequestScan(); return Accepted(); }

    [HttpDelete("devices")]
    public IActionResult Clear() { discovery.Clear(); return NoContent(); }

    [HttpDelete("devices/{mac}")]
    public IActionResult Remove(string mac) => discovery.RemoveOffline(mac) ? NoContent() : NotFound();
}
