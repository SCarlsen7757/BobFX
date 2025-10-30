namespace BobFx.Core.Services
{
    /// <summary>
    /// Logic service that coordinates RGB effects with countdown events.
    /// This is the main orchestration layer for all RGB behavior.
    /// </summary>
    public class RgbControlService
    {
        private readonly CountdownService countdownService;
        private readonly DRgbService rgbService;
        private readonly ILogger<RgbControlService> logger;

        public RgbControlService(CountdownService countdownService,
                                 DRgbService rgbService,
                                 ILogger<RgbControlService> logger)
        {
            this.countdownService = countdownService ?? throw new ArgumentNullException(nameof(countdownService));
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Subscribe to countdown events
            countdownService.OnPreCountdown += OnPreCountdown;
            countdownService.OnStartOfEvent += OnCountdownStarted;
            countdownService.OnEndOfEvent += OnCountdownEnded;

            logger.LogInformation("RgbControlService initialized and subscribed to countdown events");
        }

        /// <summary>
        /// Manually start an RGB effect (independent of countdown).
        /// </summary>
        public async Task StartEffectAsync(RgbEffect effect, TimeSpan? speed = null)
        {
            logger.LogInformation("Manually starting RGB effect: {Effect}", effect);
            rgbService.IsUdpActive = true;
            await rgbService.StartEffectAsync(effect, speed);
        }

        /// <summary>
        /// Stop all RGB effects.
        /// </summary>
        public void StopEffect()
        {
            logger.LogInformation("Stopping RGB effect");
            rgbService.IsUdpActive = false;
            rgbService.StopEffect();
        }

        /// <summary>
        /// Set the primary color for effects.
        /// </summary>
        public void SetPrimaryColor(string hexColor)
        {
            rgbService.SetPrimaryColor(hexColor);
            logger.LogDebug("Primary color set to {Color}", hexColor);
        }

        /// <summary>
        /// Set the secondary color for effects.
        /// </summary>
        public void SetSecondaryColor(string hexColor)
        {
            rgbService.SetSecondaryColor(hexColor);
            logger.LogDebug("Secondary color set to {Color}", hexColor);
        }

        /// <summary>
        /// Set the speed/delay between effect frames.
        /// </summary>
        public void SetSpeed(TimeSpan speed)
        {
            rgbService.SetSpeed(speed);
            logger.LogDebug("Speed set to {Speed}ms", speed.TotalMilliseconds);
        }

        // --- Countdown Event Handlers ---

        private void OnPreCountdown(TimeSpan duration)
        {
            logger.LogInformation("Pre-countdown started - activating fade-in effect");
            rgbService.IsUdpActive = true;
            rgbService.SetPrimaryColor("#0000ff");
            _ = rgbService.StartEffectAsync(RgbEffect.FadeIn, TimeSpan.FromMilliseconds(50));
        }

        private void OnCountdownStarted(TimeSpan duration)
        {
            logger.LogInformation("Countdown started - activating green strobe");
            rgbService.IsUdpActive = true;
            rgbService.SetPrimaryColor("#00ff00");
            rgbService.SetSecondaryColor("#a2ff00");
            _ = rgbService.StartEffectAsync(RgbEffect.Strobe, TimeSpan.FromMilliseconds(250));

            // Switch to solid green after 5 seconds
            _ = SwitchToRunningEffectAsync();
        }

        private async Task SwitchToRunningEffectAsync()
        {
            await Task.Delay(5000);
            if (countdownService.IsRunning)
            {
                logger.LogInformation("Switching to solid green (running state)");
                await rgbService.StartEffectAsync(RgbEffect.Solid);
            }
        }

        private void OnCountdownEnded()
        {
            logger.LogInformation("Countdown ended - activating red strobe");
            rgbService.SetPrimaryColor("#ff0000");
            rgbService.SetSecondaryColor("#ffff00");
            _ = rgbService.StartEffectAsync(RgbEffect.Strobe, TimeSpan.FromMilliseconds(250));

            // Stop UDP after showing red strobe for 5 seconds
            _ = StopUdpAfterDelayAsync();
        }

        private async Task StopUdpAfterDelayAsync()
        {
            await Task.Delay(5000);
            logger.LogInformation("Stopping RGB after countdown end delay");
            rgbService.IsUdpActive = false;
            rgbService.StopEffect();
        }
    }
}
