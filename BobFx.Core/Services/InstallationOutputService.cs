using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace BobFx.Core.Services;

public sealed record CalibrationView(string SessionId, string ActiveBoardId, DateTimeOffset ExpiresUtc);
public sealed record CalibrationRequest(IReadOnlyList<BoardSettings> Boards, string ActiveBoardId);
public sealed record LightShowTestView(string Segment, DateTimeOffset EndsUtc);
public sealed record LightShowTestRequest(string Segment, LightShowSettings LightShow, AnimationSettings Animation);

public sealed class InstallationOutputService(InstallationSettingsStore settings, WLedDiscoveryService discovery,
    RgbAndSoundControlService control, DRgbService rgb, WLedIdleSync idleSync, TimeProvider clock,
    ILogger<InstallationOutputService> logger, IWLedFrameTransport transport, BoardFrameMonitor monitor) : BackgroundService
{
    private readonly ConcurrentDictionary<string, string> errors = new();
    public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>(errors);
    private long lastSendTicks;
    public DateTimeOffset? LastSendUtc => Volatile.Read(ref lastSendTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    private string? lastError;
    /// <summary>Written by the output loop and read by health requests on other threads.</summary>
    public string? LastError { get => Volatile.Read(ref lastError); private set => Volatile.Write(ref lastError, value); }
    private readonly CancellationTokenSource locateStop = new();
    private Task? locateTask;
    private int disposed;
    private int stopping;
    private readonly SemaphoreSlim calibrationFrames = new(1, 1);
    /// <summary>Even a frame that overran leaves the loop a moment for commands and shutdown.</summary>
    private static readonly TimeSpan MinimumFrameWait = TimeSpan.FromMilliseconds(1);
    private sealed record CalibrationDraft(CalibrationView View, InstallationSettings Settings, long Started);
    private CalibrationDraft? calibration;
    public CalibrationView? Calibration => Volatile.Read(ref calibration)?.View;
    private sealed record LightTest(LightShowTestView View, InstallationSettings Settings, long Started);
    private LightTest? lightTest;
    public LightShowTestView? LightShowTest => Volatile.Read(ref lightTest)?.View;

    /// <summary>Plays one moment of the light show on the real boards from unsaved settings. The test ends
    /// by itself, so a closed browser cannot leave the boards lit; a new test replaces a running one.</summary>
    public Task StartLightShowTestAsync(LightShowTestRequest request) => control.LightTestCommandAsync(() =>
    {
        if (Volatile.Read(ref stopping) != 0) throw new InvalidOperationException("Installation output is shutting down.");
        if (request.LightShow is null || request.Animation is null) throw new ArgumentException("Light show and animation settings are required.");
        var draft = settings.Snapshot with { LightShow = request.LightShow, Animation = request.Animation };
        InstallationSettingsStore.Validate(draft);
        var seconds = BoardLightShow.TestSeconds(request.Segment ?? "", draft.Animation)
            ?? throw new ArgumentException("Choose countdown, playing or gameOver to test.");
        if (BoardSceneRenderer.Scene(draft).Boards.Count == 0) throw new ArgumentException("Assign at least one board before testing the light show.");
        Volatile.Write(ref lightTest, new(new(request.Segment!, clock.GetUtcNow().AddSeconds(seconds)), draft, clock.GetTimestamp()));
        return true;
    }, starting: true);

    /// <summary>Stops the running test. With <paramref name="onlyIfStarted"/>, a test that replaced the
    /// expired one is left playing.</summary>
    public Task StopLightShowTestAsync(long? onlyIfStarted = null) => control.LightTestCommandAsync(() =>
    {
        var current = Volatile.Read(ref lightTest);
        if (current is not null && (onlyIfStarted is null || onlyIfStarted == current.Started)) Volatile.Write(ref lightTest, null);
        return Volatile.Read(ref lightTest) is not null;
    });

    private InstallationSettings ValidateCalibration(CalibrationRequest request)
    {
        // The session keeps its own list: the caller's may change while the check runs.
        var snapshot = settings.Snapshot with { Boards = request.Boards is null ? null! : [.. request.Boards] };
        InstallationSettingsStore.Validate(snapshot);
        if (!snapshot.Boards.Any(b => b.Id == request.ActiveBoardId && b.DeviceMac is not null))
            throw new ArgumentException("Choose an assigned board to calibrate.");
        return snapshot;
    }
    public Task StartCalibrationAsync(CalibrationRequest request) => control.CalibrationCommandAsync(async () =>
    {
        if (Volatile.Read(ref stopping) != 0) throw new InvalidOperationException("Installation output is shutting down.");
        var snapshot = ValidateCalibration(request);
        await calibrationFrames.WaitAsync();
        try
        {
            Volatile.Write(ref calibration, new(new(Guid.NewGuid().ToString("N"), request.ActiveBoardId,
                clock.GetUtcNow().AddSeconds(15)), snapshot, clock.GetTimestamp()));
        }
        finally { calibrationFrames.Release(); }
    }, starting: true);

    public Task UpdateCalibrationAsync(string id, CalibrationRequest request) => control.CalibrationCommandAsync(async () =>
    {
        var snapshot = ValidateCalibration(request);
        await calibrationFrames.WaitAsync();
        try
        {
            var current = calibration;
            if (current?.View.SessionId != id || current.View.ExpiresUtc <= clock.GetUtcNow())
                throw new InvalidOperationException("Calibration has ended or belongs to another session.");
            // Keep membership fixed while previewing, so removed devices cannot be left illuminated.
            if (!snapshot.Boards.Where(b => b.DeviceMac is not null).Select(b => (b.Id, b.DeviceMac)).ToHashSet()
                .SetEquals(current.Settings.Boards.Where(b => b.DeviceMac is not null).Select(b => (b.Id, b.DeviceMac))))
                throw new ArgumentException("Stop calibration before changing controller assignments.");
            Volatile.Write(ref calibration, current with { Settings = snapshot,
                View = current.View with { ActiveBoardId = request.ActiveBoardId, ExpiresUtc = clock.GetUtcNow().AddSeconds(15) } });
        }
        finally { calibrationFrames.Release(); }
    });

    public Task StopCalibrationAsync(string id, bool expiredOnly = false)
    {
        if (Calibration is null) return Task.CompletedTask;
        return control.CalibrationCommandAsync(async () =>
        {
            await calibrationFrames.WaitAsync();
            try
            {
                if (calibration?.View.SessionId != id) throw new InvalidOperationException("Calibration belongs to another session.");
                if (expiredOnly && calibration.View.ExpiresUtc > clock.GetUtcNow())
                    throw new InvalidOperationException("Calibration was renewed.");
                Volatile.Write(ref calibration, null);
            }
            finally { calibrationFrames.Release(); }
        }, stopping: true);
    }

    private async Task CalibrationFrameAsync(CancellationToken token)
    {
        await calibrationFrames.WaitAsync(token);
        try
        {
            var current = calibration;
            if (current is null || current.View.ExpiresUtc <= clock.GetUtcNow()) return;
            var scene = BoardSceneRenderer.Scene(current.Settings);
            foreach (var board in scene.Boards)
            {
                var device = discovery.Resolve(scene.Device(board));
                // During a check the operator is looking at the boards, so a board that stays dark is explained.
                if (discovery.KnownOffline(device)) errors[device.Mac] = "Device is offline.";
                else if (Fits(board, device))
                    await TrySendAsync(device, board.Id == current.View.ActiveBoardId
                        ? BoardSceneRenderer.Calibration(board, device.LedCount, clock.GetElapsedTime(current.Started).TotalSeconds)
                        : new byte[device.LedCount * 3], token);
            }
        }
        finally { calibrationFrames.Release(); }
    }
    /// <summary>Sends one frame of the game scene to every assigned board that is not known to be offline.</summary>
    private async Task SendSceneAsync(InstallationSettings source, GamePhase phase, double elapsed, double preparation, CancellationToken token)
    {
        var scene = BoardSceneRenderer.Scene(source);
        foreach (var board in scene.Boards)
        {
            var saved = scene.Device(board);
            if (discovery.KnownOffline(saved)) continue;
            var device = discovery.Resolve(saved);
            if (Fits(board, device)) await TrySendAsync(device, scene.Render(board, device.LedCount, phase, elapsed, preparation), token);
        }
    }
    /// <summary>Whether the controller still has the LEDs its board is mapped onto. A controller that reports
    /// fewer than when the board was mapped gets no frame and an error that says what to do.</summary>
    private bool Fits(BoardSettings? board, DeviceSettings device)
    {
        if (board is null || BoardMapping.Fits(board, device.LedCount)) return true;
        errors[device.Mac] = BoardSceneRenderer.MappingChanged;
        return false;
    }
    /// <summary>Sends a frame to one controller and records how it went. A failure is that controller's alone:
    /// the others still get their frames, and its error is cleared by its next good frame.</summary>
    private async Task TrySendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        try
        {
            await SendAsync(device, pixels, token);
            errors.TryRemove(device.Mac, out _);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
        { errors[device.Mac] = ex.Message; }
    }
    /// <summary>Every frame leaves through here. A simulated controller has no network side, so its frame
    /// goes to the monitor only; a real controller's frame is sent and mirrored there.</summary>
    private async Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        if (!device.Simulated) await transport.SendAsync(device, pixels, token);
        monitor.Record(device.Mac, pixels);
        Volatile.Write(ref lastSendTicks, clock.GetUtcNow().UtcTicks);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await control.ConfigureAsync(() => ApplyIdleSyncAsync(stoppingToken)); }
        catch (InvalidOperationException) { /* Output already belongs to an operator. */ }
        Volatile.Write(ref ready, 1);
        // Shutdown may cancel the loop's retry delay; the service must still report that it is no longer ready.
        try { await RunOutputLoopAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels the retry delay. */ }
        finally { Volatile.Write(ref ready, 0); }
    }
    private async Task RunOutputLoopAsync(CancellationToken stoppingToken)
    {
        var wasActive = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var frameStarted = clock.GetTimestamp();
                var snapshot = settings.Snapshot;
                if (Calibration is { } preview)
                {
                    if (preview.ExpiresUtc <= clock.GetUtcNow())
                    {
                        try { await StopCalibrationAsync(preview.SessionId, expiredOnly: true); }
                        catch (InvalidOperationException) { /* The operator renewed or stopped it first. */ }
                    }
                    else await CalibrationFrameAsync(stoppingToken);
                }
                var test = Volatile.Read(ref lightTest);
                if (test is not null)
                {
                    if (BoardLightShow.TestScene(test.View.Segment, clock.GetElapsedTime(test.Started).TotalSeconds, test.Settings.Animation) is { } moment)
                        await SendSceneAsync(test.Settings, moment.Phase, moment.Elapsed, CountdownService.PreCountdownDuration.TotalSeconds, stoppingToken);
                    else await StopLightShowTestAsync(test.Started);
                }
                var streaming = rgb.IsUdpActive;
                // A finished test hands the boards back to WLED's idle state the same way a finished game does.
                var active = streaming || test is not null;
                if (wasActive && !active)
                {
                    try { await control.ConfigureAsync(() => ApplyIdleSyncAsync(stoppingToken, false)); }
                    catch (InvalidOperationException) { /* A new game acquired output first. */ }
                }
                wasActive = active;
                if (streaming)
                {
                    var scene = control.GetSceneFrame();
                    // The game plays on the boards only; a manual effect is mirrored to every selected device.
                    if (scene.Phase != GamePhase.Idle) await SendSceneAsync(snapshot, scene.Phase, scene.Elapsed, scene.Preparation, stoppingToken);
                    else
                    {
                        var manual = rgb.GetSnapshot();
                        foreach (var d in snapshot.Devices.Where(saved => saved.Selected).Where(saved => !discovery.KnownOffline(saved)).Select(discovery.Resolve))
                            if (Fits(snapshot.Boards.FirstOrDefault(b => b.DeviceMac == d.Mac), d))
                                await TrySendAsync(d, BoardSceneRenderer.Resample(manual, d.LedCount), stoppingToken);
                    }
                }
                // The time the frame took comes off the wait, so the frame rate does not sink as boards are added.
                var wait = TimeSpan.FromSeconds(1d / (test?.Settings ?? snapshot).Animation.FramesPerSecond) - clock.GetElapsedTime(frameStarted);
                await Task.Delay(wait > MinimumFrameWait ? wait : MinimumFrameWait, clock, stoppingToken);
                LastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex))
            {
                logger.LogWarning(ex, "Installation output failed; retrying");
                LastError = "Installation output loop failed; retrying";
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
    }
    public Task LocateAsync(string mac, CancellationToken token = default) => LocateDeviceAsync(
        settings.Snapshot.Devices.FirstOrDefault(d => d.Mac == mac) ?? throw new ArgumentException("Register this device first."), token);
    public Task LocateDeviceAsync(DeviceSettings device, CancellationToken token = default)
    {
        var task = control.LocateAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, locateStop.Token);
            var locateToken = linked.Token;
            InstallationSettingsStore.Validate(new InstallationSettings { Devices = [device] });
            var d = discovery.Resolve(device);
            if (discovery.KnownOffline(d)) throw new ArgumentException("Device is offline.");
            var started = clock.GetTimestamp();
            while (clock.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                var level = (byte)(30 + 150 * (Math.Sin(clock.GetElapsedTime(started).TotalSeconds * Math.PI * 2) + 1) / 2);
                // A new array each time: the monitor keeps the frame it is given.
                var pixels = new byte[d.LedCount * 3];
                Array.Fill(pixels, level);
                await SendAsync(d, pixels, locateToken);
                await Task.Delay(TimeSpan.FromMilliseconds(33), clock, locateToken);
            }
            // Realtime packets do not modify native presets; their one-second lease expires.
        });
        // Failed concurrent attempts must not replace the task we own on shutdown.
        if (!task.IsCompleted) locateTask = task;
        return task;
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref stopping, 1);
        // The host may dispose its services before it stops them; a disposed source was already cancelled.
        try { await locateStop.CancelAsync(); }
        catch (ObjectDisposedException) { logger.LogDebug("Locate cancellation source was already disposed"); }
        if (locateTask is { } task)
            try { await task; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { logger.LogDebug("Locate completed before shutdown with a cancellation or device failure"); }
        await base.StopAsync(cancellationToken);
        if (Calibration is { } preview) await StopCalibrationAsync(preview.SessionId);
        if (LightShowTest is not null) await StopLightShowTestAsync();
    }
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref stopping, 1);
        locateStop.Cancel(); locateStop.Dispose(); base.Dispose();
    }
    /// <summary>Sets up WLED's own sync for the idle boards; what goes wrong is reported per controller.</summary>
    public Task ApplyIdleSyncAsync(CancellationToken token = default, bool configure = true) => idleSync.ApplyAsync(
        (mac, problem) => { if (problem is null) errors.TryRemove(mac, out _); else errors[mac] = problem; }, configure, token);
}
