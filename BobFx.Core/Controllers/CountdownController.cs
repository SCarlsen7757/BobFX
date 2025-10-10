using BobFx.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BobFx.Core.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CountdownController : ControllerBase
    {
        private readonly CountdownService countdown;

        public CountdownController(CountdownService countdown)
        {
            this.countdown = countdown;
        }

        [HttpPost("start")]
        public IActionResult Start([FromQuery] int seconds = 10)
        {
            countdown.Start(TimeSpan.FromSeconds(seconds));
            return Ok(new { message = $"Countdown started for {seconds} seconds" });
        }

        [HttpPost("stop")]
        public IActionResult Stop()
        {
            countdown.Stop();
            return Ok(new { message = "Countdown stopped" });
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            return Ok(new
            {
                running = countdown.IsRunning,
                remainingSeconds = (int)countdown.Remaining.TotalSeconds
            });
        }
    }
}

