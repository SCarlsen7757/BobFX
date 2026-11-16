using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
namespace BobFx.Core.Controllers;
public sealed record StartEffectRequest(string Effect = "Rainbow", [Range(RgbEffectBase.MinSpeedMilliseconds, RgbEffectBase.MaxSpeedMilliseconds)] int? SpeedMs = null,
    string[]? Colors = null, double? FadeDurationSeconds = null, double? BreathDurationSeconds = null,
    double? MinBrightnessPercent = null, double? MaxBrightnessPercent = null);
public sealed record ChangeSpeedRequest([Range(RgbEffectBase.MinSpeedMilliseconds, RgbEffectBase.MaxSpeedMilliseconds)] int SpeedMs);
[ApiController]
[Route("api/[controller]")]
public sealed class DRgbController(DRgbService ledService, IRgbEffectFactory factory, RgbAndSoundControlService control) : ControllerBase
{
    [HttpPost("start")]
    public Task Start([FromBody] StartEffectRequest request)
    {
        if (!Enum.TryParse(request.Effect, true, out RgbEffect selected) || !Enum.IsDefined(selected)) throw new ArgumentException("Invalid effect.");
        return control.StartManualEffectAsync(b =>
        {
            b.WithEffect(selected);
            if (request.SpeedMs is int speed) b.WithSpeed(speed);
            var info = factory.Info(selected);
            if (request.Colors is { } colors)
            {
                if (colors.Length < info.RequiredColors || colors.Length > info.TotalColors)
                    throw new ArgumentException("Provide the selected effect's required colors.");
                for (var i = 0; i < colors.Length; i++)
                {
                    var color = colors[i];
                    if (!ColorHelper.TryParseHex(color, out var red, out var green, out var blue)) throw new ArgumentException("Colors must use #RRGGBB format.");
                    if (i < info.RequiredColors && (red | green | blue) == 0)
                        throw new ArgumentException("Required colors must be non-black.");
                    b.WithColor(color);
                }
            }
            ValidateDuration(request.FadeDurationSeconds);
            ValidateDuration(request.BreathDurationSeconds);
            if (request.FadeDurationSeconds is { } fade) b.WithFadeDuration(fade);
            if (request.BreathDurationSeconds is { } breath) b.WithBreathDuration(breath);
            ValidateBrightness(request.MinBrightnessPercent);
            ValidateBrightness(request.MaxBrightnessPercent);
            if (request.MinBrightnessPercent is { } min) b.WithMinBrightness(min / 100);
            if (request.MaxBrightnessPercent is { } max) b.WithMaxBrightness(max / 100);
        });
    }
    private static void ValidateDuration(double? duration)
    {
        if (duration is { } value && (!double.IsFinite(value) || value is < 0.1 or > 60))
            throw new ArgumentException("Effect duration must be 0.1–60 seconds.");
    }
    private static void ValidateBrightness(double? brightness)
    {
        if (brightness is { } value && (!double.IsFinite(value) || value is < 0 or > 100))
            throw new ArgumentException("Brightness must be 0–100%.");
    }
    [HttpPost("stop")]
    public Task Stop() => control.StopManualEffectAsync();
    [HttpPost("speed")]
    public Task ChangeSpeed([FromBody] ChangeSpeedRequest request) => control.SetManualSpeedAsync(TimeSpan.FromMilliseconds(request.SpeedMs));
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = ledService.GetStatus();
        return Ok(new { currentEffect = state.Effect.ToString(), speed = state.Speed.TotalMilliseconds, ledCount = state.LedCount,
            streaming = state.Streaming, phase = control.Phase.ToString(), error = state.LastError });
    }
    [HttpGet("effects")]
    public IActionResult GetAvailableEffects() => Ok(factory.GetRegisteredEffects().Select(type => new { type = type.ToString(), info = factory.Info(type) }).ToArray());
}
