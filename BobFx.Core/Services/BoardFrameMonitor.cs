using System.Collections.Concurrent;
using BobFx.Core.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BobFx.Core.Services;

/// <summary>What a controller's LEDs show right now: its RGB bytes, or null when it is not receiving frames
/// and has fallen back to its own idle state.</summary>
public sealed record BoardFrame(string Mac, byte[]? Pixels);

/// <summary>Remembers the last frame sent to every controller, real or simulated. It is the LED strip of a
/// simulated controller and a mirror of what a real one was sent.</summary>
public sealed class BoardFrameMonitor(TimeProvider clock)
{
    /// <summary>BobFX asks WLED to hold realtime data for one second; after that WLED resumes its own effect.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, (byte[] Pixels, long At)> frames = new();

    /// <summary>The caller must not change <paramref name="pixels"/> afterwards.</summary>
    public void Record(string mac, byte[] pixels) => frames[mac] = (pixels, clock.GetTimestamp());

    /// <summary>The frame the controller is showing, or null once its realtime lease has run out.</summary>
    public byte[]? Current(string mac) =>
        frames.TryGetValue(mac, out var frame) && clock.GetElapsedTime(frame.At) < Lease ? frame.Pixels : null;
}

/// <summary>Streams the monitored frames to browsers that show the live boards. Nothing is sent while no
/// browser is subscribed or while the picture does not change.</summary>
public sealed class BoardMonitorService(BoardFrameMonitor monitor, InstallationSettingsStore settings, IHubContext<UiHub> hub,
    TimeProvider clock, ILogger<BoardMonitorService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> clients = new();
    private int refresh;

    public void Subscribe(string connectionId) { clients.TryAdd(connectionId, 0); Volatile.Write(ref refresh, 1); }
    public void Unsubscribe(string connectionId) => clients.TryRemove(connectionId, out _);

    /// <summary>One entry per selected controller, in the order the settings list them.</summary>
    public BoardFrame[] Snapshot() => settings.Snapshot.Devices.Where(d => d.Selected).Select(d => new BoardFrame(d.Mac, monitor.Current(d.Mac))).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels the retry delay. */ }
    }
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        BoardFrame[] sent = [];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1d / settings.Snapshot.Animation.FramesPerSecond), clock, stoppingToken);
                if (clients.IsEmpty) { sent = []; continue; }
                var frames = Snapshot();
                // Frames are replaced, never changed in place, so the same array means the same picture.
                var changed = frames.Length != sent.Length || frames.Zip(sent).Any(pair => pair.First.Mac != pair.Second.Mac || !ReferenceEquals(pair.First.Pixels, pair.Second.Pixels));
                if (!changed && Interlocked.Exchange(ref refresh, 0) == 0) continue;
                Volatile.Write(ref refresh, 0);
                await hub.Clients.Group(UiHub.BoardsGroup).SendAsync("BoardFramesChanged", frames, stoppingToken);
                sent = frames;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            {
                logger.LogWarning(ex, "Live board update failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
    }
}
