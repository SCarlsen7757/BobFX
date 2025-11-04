using Microsoft.Extensions.Options;

namespace BobFx.Core.Services
{
    public class CountdownService
    {
        private readonly Lock @lock = new();
        private readonly ILogger<CountdownService> logger;
        private CancellationTokenSource? cts;
        private TimeSpan remaining = TimeSpan.Zero;
        private TimeSpan preCountdownRemaining = TimeSpan.Zero;

        public event Action? OnTick;
        public event Action<TimeSpan>? OnStartOfEvent;
        public event Action? OnEndOfEvent;
        public event Action<TimeSpan>? OnPreCountdown;

        public bool IsRunning { get; private set; } = false;
        public bool IsPreCountdownRunning { get; private set; } = false;
        public TimeSpan Remaining => remaining;
        public TimeSpan PreCountdownRemaining => preCountdownRemaining;

        private readonly CountdownOptions options;

        public TimeSpan PreCountdownDuration { get => options.PreCountdownDuration; }
        public TimeSpan CountdownDuration { get => options.CountdownDuration; }
        public TimeSpan CountdownDeviation { get => options.CountdownDeviation; }

        public CountdownService(ILogger<CountdownService> logger,
                                IOptions<CountdownOptions> options)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            logger.LogInformation("CountdownService initialized");
        }

        public TimeSpan Start()
        {
            return Start(CountdownDuration, CountdownDeviation);
        }

        public TimeSpan Start(TimeSpan duration, TimeSpan deviation)
        {
            lock (@lock)
            {
                if (IsRunning)
                {
                    logger.LogDebug("Countdown Start called but already running");
                    return remaining;
                }

                var deviationSeconds = (int)deviation.TotalSeconds;
                var randomOffset = Random.Shared.Next(-deviationSeconds, deviationSeconds + 1);
                var actualDuration = duration.Add(TimeSpan.FromSeconds(randomOffset));

                remaining = actualDuration;
                cts = new CancellationTokenSource();
                IsRunning = true;
                logger.LogInformation("Countdown started for {Seconds} seconds (base: {Base}, offset: {Offset})",
                    actualDuration.TotalSeconds, duration.TotalSeconds, randomOffset);
                OnStartOfEvent?.Invoke(actualDuration);
                OnTick?.Invoke();
                _ = RunCountdownAsync(cts.Token);

                return actualDuration;
            }
        }

        public async Task StartWithPreCountdownAsync()
        {
            await StartWithPreCountdownAsync(PreCountdownDuration, CountdownDuration, CountdownDeviation);
        }

        public async Task StartWithPreCountdownAsync(TimeSpan preCountdownDuration, TimeSpan countdownDuration, TimeSpan countdownDeviation)
        {
            lock (@lock)
            {
                if (IsPreCountdownRunning || IsRunning)
                {
                    logger.LogDebug("StartWithPreCountdown called but already running");
                    return;
                }

                cts = new CancellationTokenSource();
                IsPreCountdownRunning = true;
                preCountdownRemaining = preCountdownDuration;
            }

            OnPreCountdown?.Invoke(preCountdownDuration);
            OnTick?.Invoke();
            logger.LogInformation("Pre-countdown started for {Seconds} seconds", preCountdownDuration.TotalSeconds);

            try
            {
                while (preCountdownRemaining > TimeSpan.Zero && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                    preCountdownRemaining = preCountdownRemaining.Subtract(TimeSpan.FromSeconds(1));
                    logger.LogDebug("Pre-countdown tick: {Remaining}s", preCountdownRemaining.TotalSeconds);
                    OnTick?.Invoke();
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    lock (@lock)
                    {
                        IsPreCountdownRunning = false;
                        preCountdownRemaining = TimeSpan.Zero;
                    }
                    logger.LogInformation("Pre-countdown completed");
                    Start(countdownDuration, countdownDeviation);
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Pre-countdown cancelled");
                lock (@lock)
                {
                    IsPreCountdownRunning = false;
                    preCountdownRemaining = TimeSpan.Zero;
                }
                OnTick?.Invoke();
            }
        }

        public void Stop()
        {
            lock (@lock)
            {
                if (!IsRunning && !IsPreCountdownRunning)
                {
                    logger.LogDebug("Countdown Stop called but not running");
                    return;
                }

                cts?.Cancel();
                IsRunning = false;
                IsPreCountdownRunning = false;
                remaining = TimeSpan.Zero;
                preCountdownRemaining = TimeSpan.Zero;
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
                    IsRunning = false;
                }
                OnTick?.Invoke();
                OnEndOfEvent?.Invoke();
                logger.LogInformation("Countdown ended");
            }
        }
    }
}
