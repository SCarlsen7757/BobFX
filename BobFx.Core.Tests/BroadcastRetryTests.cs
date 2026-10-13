using System.Collections.Concurrent;
using System.Net.Sockets;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
namespace BobFx.Core.Tests;

public sealed class BroadcastRetryTests
{
    [Fact]
    public async Task PersistentFailureRetriesOncePerSecondAndLogsOnlyTransitions()
    {
        var clock = new TrackingClock();
        var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        rgb.IsUdpActive = true;
        using var udp = new FakeUdp(options, (attempt, _) =>
            attempt <= 2 ? Task.FromException(new SocketException((int)SocketError.NetworkUnreachable)) : Task.CompletedTask);
        var logger = new RecordingLogger();
        using var broadcast = new WLedBroadcastService(logger, options, rgb, udp, clock);
        await broadcast.StartAsync(CancellationToken.None);
        await Eventually(() => clock.TimerCount >= 1);
        Assert.Equal(1, udp.Attempts);
        Assert.NotNull(broadcast.LastError);
        Assert.Null(broadcast.LastSendUtc);

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, udp.Attempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually(() => clock.TimerCount >= 2);
        Assert.Equal(2, udp.Attempts);
        Assert.Equal(1, logger.Messages.Count(item => item.Level == LogLevel.Warning));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => clock.TimerCount >= 3);
        Assert.Equal(3, udp.Attempts);
        Assert.Null(broadcast.LastError);
        Assert.Equal(clock.GetUtcNow(), broadcast.LastSendUtc);
        Assert.Equal(1, logger.Messages.Count(item => item.Message.Contains("recovered", StringComparison.Ordinal)));

        clock.Advance(TimeSpan.FromMilliseconds(16));
        await Eventually(() => clock.TimerCount >= 4);
        Assert.Equal(4, udp.Attempts);
        Assert.Equal(1, logger.Messages.Count(item => item.Level == LogLevel.Warning));
        Assert.Equal(1, logger.Messages.Count(item => item.Message.Contains("recovered", StringComparison.Ordinal)));
        await broadcast.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationDuringSendDoesNotRecordTransportFailure()
    {
        var clock = new FakeTimeProvider();
        var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        rgb.IsUdpActive = true;
        using var udp = new FakeUdp(options, (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var logger = new RecordingLogger();
        using var broadcast = new WLedBroadcastService(logger, options, rgb, udp, clock);
        await broadcast.StartAsync(CancellationToken.None);
        await Eventually(() => udp.Attempts == 1);
        await broadcast.StopAsync(CancellationToken.None);
        Assert.Null(broadcast.LastError);
        Assert.Null(broadcast.LastSendUtc);
        Assert.DoesNotContain(logger.Messages, item => item.Level >= LogLevel.Warning);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private sealed class TrackingClock : FakeTimeProvider
    {
        private int timerCount;
        public int TimerCount => Volatile.Read(ref timerCount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref timerCount);
            return timer;
        }
    }
    private sealed class FakeUdp(IOptions<WLedOptions> options, Func<int, CancellationToken, Task> send)
        : UdpClientService(NullLogger<UdpClientService>.Instance, options)
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public override Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            send(Interlocked.Increment(ref attempts), cancellationToken);
    }
    private sealed class RecordingLogger : ILogger<WLedBroadcastService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue((logLevel, formatter(state, exception)));
    }
}
