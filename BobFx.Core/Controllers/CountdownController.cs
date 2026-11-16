using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
namespace BobFx.Core.Controllers;
public sealed record StartGameRequest(int? DurationSeconds = null, int? DeviationSeconds = null);
[ApiController]
[Route("api/[controller]")]
public sealed class CountdownController(CountdownService countdown, GameCoordinator control) : ControllerBase
{
    [HttpPost("start")]
    public Task Start([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartGameRequest? request = null) =>
        control.StartGameAsync(Seconds(request?.DurationSeconds), Seconds(request?.DeviationSeconds));
    private static TimeSpan? Seconds(int? seconds) => seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null;
    [HttpPost("stop")]
    public Task Stop() => control.StopGameAsync();
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = countdown.Snapshot;
        return Ok(new { phase = control.Phase.ToString(), running = state.IsRunning, preCountdownRunning = state.IsPreCountdownRunning,
            remainingSeconds = (int)Math.Ceiling(state.Remaining.TotalSeconds), preRemainingSeconds = (int)Math.Ceiling(state.PreCountdownRemaining.TotalSeconds) });
    }
}
