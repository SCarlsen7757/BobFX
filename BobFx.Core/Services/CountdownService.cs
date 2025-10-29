using Microsoft.Extensions.Logging;

namespace BobFx.Core.Services
{
    public class CountdownService
    {
        private readonly Lock @lock = new();
        private readonly ILogger<CountdownService> logger;
        private CancellationTokenSource? cts;
        private TimeSpan remaining = TimeSpan.Zero;

        public event Action? OnTick;
        public event Action? OnStartOfEvent;
        public event Action? OnEndOfEvent;
        public event Action? OnPreCountdown;

        public bool IsRunning { get; private set; } = false;
        public bool IsPreCountdownRunning { get; private set; } = false;
        public TimeSpan Remaining => remaining;

        public CountdownService(ILogger<CountdownService> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            logger.LogInformation("CountdownService initialized");
        }

        public void Start(TimeSpan duration)
        {
            lock (@lock)
            {
                if (IsRunning)
                {
                    logger.LogDebug("Countdown Start called but already running");
                    return;
                }

                remaining = duration;
                cts = new CancellationTokenSource();
                IsRunning = true;
                logger.LogInformation("Countdown started for {Seconds} seconds", duration.TotalSeconds);
                OnStartOfEvent?.Invoke();
                OnTick?.Invoke();
                _ = RunCountdownAsync(cts.Token);
            }
        }

        public async Task StartWithPreCountdownAsync(TimeSpan preDuration, TimeSpan mainDuration)
        {
            IsPreCountdownRunning = true;
            OnPreCountdown?.Invoke();
            await Task.Delay(preDuration);
            IsPreCountdownRunning = false;
            Start(mainDuration);
        }

        public void Stop()
        {
            lock (@lock)
            {
                if (!IsRunning)
                {
                    logger.LogDebug("Countdown Stop called but not running");
                    return;
                }

                cts?.Cancel();
                IsRunning = false;
                remaining = TimeSpan.Zero;
                logger.LogInformation("Countdown stopped");
                OnCountdownEnded();
            }
        }

        private async Task RunCountdownAsync(CancellationToken token)
        {
            try
            {
                while (remaining > TimeSpan.Zero && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    remaining = remaining.Subtract(TimeSpan.FromSeconds(1));
                    logger.LogDebug("Countdown tick: {Remaining}s", remaining.TotalSeconds);
                    OnTick?.Invoke();
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Countdown task cancelled");
            }
            finally
            {
                lock (@lock)
                {
                    IsRunning = false;
                }
                OnTick?.Invoke();
                OnEndOfEvent?.Invoke();
                logger.LogInformation("Countdown ended");
            }
        }

        private void OnCountdownStarted()
        {
        }

        private void OnCountdownEnded()
        {
        }
    }
}
