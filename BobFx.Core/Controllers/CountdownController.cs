using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
namespace BobFx.Core.Controllers;
public sealed record StartGameRequest(int? PreDurationSeconds = null, int? DurationSeconds = null, int? DeviationSeconds = null);
[ApiController]
[Route("api/[controller]")]
public sealed class CountdownController(CountdownService countdown, RgbAndSoundControlService control) : ControllerBase
{
    [HttpPost("start")]
    public Task<IActionResult> Start([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartGameRequest? request = null) => StartCommand(false, request);
    [HttpPost("start-with-pre")]
    public Task<IActionResult> StartWithPre([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartGameRequest? request = null) => StartCommand(true, request);
    private Task<IActionResult> StartCommand(bool pre, StartGameRequest? request) => Command(() => control.StartGameAsync(pre,
        Seconds(request?.PreDurationSeconds), Seconds(request?.DurationSeconds), Seconds(request?.DeviationSeconds)));
    private static TimeSpan? Seconds(int? seconds) => seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null;
    [HttpPost("stop")]
    public Task<IActionResult> Stop() => Command(control.StopGameAsync);
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = countdown.Snapshot;
        return Ok(new { phase = control.Phase.ToString(), running = state.IsRunning, preCountdownRunning = state.IsPreCountdownRunning,
            remainingSeconds = (int)Math.Ceiling(state.Remaining.TotalSeconds), preRemainingSeconds = (int)Math.Ceiling(state.PreCountdownRemaining.TotalSeconds) });
    }
    private async Task<IActionResult> Command(Func<Task> command)
    {
        try { await command(); return Ok(); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: "Invalid game command", detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: "Game command conflicts with current state", detail: ex.Message); }
    }
}
