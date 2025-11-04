using BobFx.Core.Services.Effects;

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
        private CancellationTokenSource? switchEffectCts;
        private CancellationTokenSource? stopUdpCts;

        public RgbControlService(ILogger<RgbControlService> logger,
                                 CountdownService countdownService,
                                 DRgbService rgbService)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.countdownService = countdownService ?? throw new ArgumentNullException(nameof(countdownService));
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));

            // Subscribe to countdown events
            countdownService.OnPreCountdown += OnPreCountdown;
            countdownService.OnStartOfEvent += OnCountdownStarted;
            countdownService.OnEndOfEvent += OnCountdownEnded;

            logger.LogInformation("RgbControlService initialized and subscribed to countdown events");
        }

        // --- Countdown Event Handlers ---

        private void OnPreCountdown(TimeSpan duration)
        {
            // Cancel any pending switch effect or stop UDP operations
            CancelSwitchEffect();
            CancelStopUdp();

            rgbService.IsUdpActive = true;

            _ = rgbService.StartEffectAsync(builder =>
            {
                builder.WithEffect(RgbEffect.FadeIn)
                    .WithColor("#0000ff") // Blue
                    .WithSpeed(10)
                    .WithFadeDuration(duration);
            });
        }

        private void OnCountdownStarted(TimeSpan duration)
        {
            // Cancel any pending switch effect or stop UDP operations
            CancelSwitchEffect();
            CancelStopUdp();

            rgbService.IsUdpActive = true;

            _ = rgbService.StartEffectAsync(builder =>
            {
                builder.WithEffect(RgbEffect.Blink)
                    .WithColor("#00ff00")
                    .WithColor("#a2ff00")
                    .WithSpeed(200);
            });

            // Switch to solid green after 5 seconds
            _ = SwitchToRunningEffectAsync();
        }

        private async Task SwitchToRunningEffectAsync()
        {
            // Create a new cancellation token for this switch operation
            switchEffectCts?.Dispose();
            switchEffectCts = new CancellationTokenSource();

            try
            {
                await Task.Delay(5000, switchEffectCts.Token);

                if (countdownService.IsRunning)
                {
                    await rgbService.StartEffectAsync(builder =>
                    {
                        builder.WithEffect(RgbEffect.Twinkle)
                            .WithColor("#a2ff00");
                    });
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Switch to running effect was cancelled");
            }
        }

        private void OnCountdownEnded()
        {
            // Cancel any pending switch effect
            CancelSwitchEffect();

            _ = rgbService.StartEffectAsync(builder =>
            {
                builder.WithEffect(RgbEffect.Blink)
                    .WithColor("#ff0000")
                    .WithColor("#ffff00")
                    .WithSpeed(250);
            });

            // Stop UDP after showing red strobe for 5 seconds
            _ = StopUdpAfterDelayAsync();
        }

        private async Task StopUdpAfterDelayAsync()
        {
            // Create a new cancellation token for this stop operation
            stopUdpCts?.Dispose();
            stopUdpCts = new CancellationTokenSource();

            try
            {
                await Task.Delay(5000, stopUdpCts.Token);
                logger.LogInformation("Stopping RGB after countdown end delay");
                rgbService.IsUdpActive = false;
                rgbService.StopEffect();
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Stop UDP after delay was cancelled");
            }
        }

        private void CancelSwitchEffect()
        {
            if (switchEffectCts != null && !switchEffectCts.IsCancellationRequested)
            {
                logger.LogDebug("Cancelling pending switch effect");
                switchEffectCts.Cancel();
            }
        }

        private void CancelStopUdp()
        {
            if (stopUdpCts != null && !stopUdpCts.IsCancellationRequested)
            {
                logger.LogDebug("Cancelling pending stop UDP");
                stopUdpCts.Cancel();
            }
        }
    }
}
