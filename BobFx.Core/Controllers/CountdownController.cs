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
        public IActionResult Start()
        {
            countdown.Start();
            return Ok();
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

        [HttpPost("start-with-pre")]
        public async Task<IActionResult> StartWithPre()
        {
            await countdown.StartWithPreCountdownAsync();
            return Ok();
        }
    }
}

