using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BobFx.Core.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DRgbController(DRgbService ledService) : ControllerBase
    {
        private readonly DRgbService ledService = ledService;

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromQuery] string effect = "Rainbow", [FromQuery] int speedMs = 100)
        {
            if (!Enum.TryParse(effect, true, out RgbEffect ledEffect))
                return BadRequest("Invalid effect");
            if (speedMs < 10) return BadRequest("Speed lower then 10ms");
            var speed = TimeSpan.FromMilliseconds(speedMs);

            await ledService.StartEffectAsync(ledEffect, speed);
            return Ok(new { message = $"Effect {ledEffect} started at {speed.TotalMilliseconds}ms" });
        }

        [HttpPost("stop")]
        public IActionResult Stop()
        {
            ledService.StopEffect();
            return Ok(new { message = "LED stopped" });
        }

        [HttpPost("speed")]
        public IActionResult ChangeSpeed([FromQuery] int speedMs = 100)
        {
            if (speedMs < 10) return BadRequest("Speed lower then 10ms");
            var speed = TimeSpan.FromMilliseconds(speedMs);
            ledService.SetSpeed(speed);
            return Ok(new { message = $"LED speed changed to {speedMs}" });
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            return Ok(new
            {
                currentEffect = ledService.CurrentEffect.ToString(),
                speed = ledService.Speed,
                ledCount = ledService.Leds.Length
            });
        }
    }
}
