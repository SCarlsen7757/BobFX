namespace BobFx.Core.Services
{
    public class CountdownService
    {
        private readonly Lock @lock = new();
        private CancellationTokenSource? cts;
        private TimeSpan remaining = TimeSpan.Zero;
        private bool isRunning;

        public event Action? OnTick;
        public event Action? OnStartOfEvent;
        public event Action? OnEndOfEvent;

        public bool IsRunning => isRunning;
        public TimeSpan Remaining => remaining;

        public void Start(TimeSpan duration)
        {
            lock (@lock)
            {
                if (isRunning) return;

                remaining = duration;
                cts = new CancellationTokenSource();
                isRunning = true;
                OnStartOfEvent?.Invoke();
                OnTick?.Invoke();
                _ = RunCountdownAsync(cts.Token);
            }
        }

        public void Stop()
        {
            lock (@lock)
            {
                cts?.Cancel();
                isRunning = false;
                remaining = TimeSpan.Zero;
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
                    OnTick?.Invoke();
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                lock (@lock)
                {
                    isRunning = false;
                }
                OnTick?.Invoke();
                OnEndOfEvent?.Invoke();
            }
        }
    }
}
