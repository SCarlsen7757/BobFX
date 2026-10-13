using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
namespace BobFx.Core.Controllers;
public sealed record StartEffectRequest(string Effect = "Rainbow", [Range(10, 60000)] int? SpeedMs = null);
public sealed record ChangeSpeedRequest([Range(10, 60000)] int SpeedMs);
[ApiController]
[Route("api/[controller]")]
public sealed class DRgbController(DRgbService ledService, IRgbEffectFactory factory, RgbAndSoundControlService control) : ControllerBase
{
    [HttpPost("start")]
    public Task<IActionResult> Start([FromBody] StartEffectRequest request)
    {
        if (!Enum.TryParse(request.Effect, true, out RgbEffect selected) || !Enum.IsDefined(selected))
            return Task.FromResult<IActionResult>(Problem(statusCode: 400, title: "Invalid effect"));
        return Command(() => control.StartManualEffectAsync(b =>
        {
            b.WithEffect(selected);
            if (request.SpeedMs is int speed) b.WithSpeed(speed);
        }));
    }
    [HttpPost("stop")]
    public Task<IActionResult> Stop() => Command(control.StopManualEffectAsync);
    [HttpPost("speed")]
    public Task<IActionResult> ChangeSpeed([FromBody] ChangeSpeedRequest request) =>
        Command(() => control.SetManualSpeedAsync(TimeSpan.FromMilliseconds(request.SpeedMs)));
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = ledService.GetStatus();
        return Ok(new { currentEffect = state.Effect.ToString(), speed = state.Speed.TotalMilliseconds, ledCount = state.LedCount,
            streaming = state.Streaming, phase = control.Phase.ToString(), error = state.LastError });
    }
    [HttpGet("effects")]
    public IActionResult GetAvailableEffects() => Ok(factory.GetRegisteredEffects().Select(type => new { type = type.ToString(), info = factory.Create(type).GetInfo() }).ToArray());
    private async Task<IActionResult> Command(Func<Task> command)
    {
        try { await command(); return Ok(); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: "Invalid effect command", detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: "Game owns the lights", detail: ex.Message); }
    }
}
