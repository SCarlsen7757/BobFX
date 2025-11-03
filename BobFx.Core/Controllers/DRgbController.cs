using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Mvc;

namespace BobFx.Core.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DRgbController(DRgbService ledService, IRgbEffectFactory effectFactory) : ControllerBase
    {
        private readonly DRgbService ledService = ledService;
        private readonly IRgbEffectFactory effectFactory = effectFactory;

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromQuery] string effect = "Rainbow", [FromQuery] int speedMs = 100)
        {
            if (!Enum.TryParse(effect, true, out RgbEffect ledEffect))
                return BadRequest("Invalid effect");
            if (speedMs < 10) return BadRequest("Speed lower than 10ms");

            try
            {
                await ledService.StartEffectAsync(builder =>
               {
                   builder.WithEffect(ledEffect)
             .WithSpeed(speedMs);
               });

                return Ok(new { message = $"Effect {ledEffect} started at {speedMs}ms" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
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
            if (speedMs < 10) return BadRequest("Speed lower than 10ms");
            var speed = TimeSpan.FromMilliseconds(speedMs);
            ledService.SetSpeed(speed);
            return Ok(new { message = $"LED speed changed to {speedMs}ms" });
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            return Ok(new
            {
                currentEffect = ledService.CurrentEffect.ToString(),
                speed = ledService.Speed.TotalMilliseconds,
                ledCount = ledService.LedCount
            });
        }

        [HttpGet("effects")]
        public IActionResult GetAvailableEffects()
        {
            var effects = effectFactory.GetRegisteredEffects()
    .Select(e => new
    {
        type = e.ToString(),
        info = RgbEffectDefaults.EffectInfo.TryGetValue(e, out var info) ? info : null
    });

            return Ok(effects);
        }
    }
}
