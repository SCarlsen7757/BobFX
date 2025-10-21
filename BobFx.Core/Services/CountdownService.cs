namespace BobFx.Core.Services
{
    public class CountdownService
    {
        private readonly Lock @lock = new();
        private readonly ILogger<CountdownService> logger;
        private CancellationTokenSource? cts;
        private TimeSpan remaining = TimeSpan.Zero;
        private bool isRunning;

        public event Action? OnTick;
        public event Action? OnStartOfEvent;
        public event Action? OnEndOfEvent;

        public bool IsRunning => isRunning;
        public TimeSpan Remaining => remaining;

        public CountdownService(ILogger<CountdownService> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start(TimeSpan duration)
        {
            lock (@lock)
            {
                if (isRunning)
                {
                    logger.LogDebug("Countdown Start called but already running");
                    return;
                }

                remaining = duration;
                cts = new CancellationTokenSource();
                isRunning = true;
                logger.LogInformation("Countdown started for {Seconds} seconds", duration.TotalSeconds);
                OnStartOfEvent?.Invoke();
                OnTick?.Invoke();
                _ = RunCountdownAsync(cts.Token);
            }
        }

        public void Stop()
        {
            lock (@lock)
            {
                if (!isRunning)
                {
                    logger.LogDebug("Countdown Stop called but not running");
                }

                cts?.Cancel();
                isRunning = false;
                remaining = TimeSpan.Zero;
                logger.LogInformation("Countdown stopped");
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
                    isRunning = false;
                }
                OnTick?.Invoke();
                OnEndOfEvent?.Invoke();
                logger.LogInformation("Countdown ended");
            }
        }
    }
}
