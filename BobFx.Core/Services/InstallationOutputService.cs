using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;

namespace BobFx.Core.Services;

public sealed record CalibrationView(string SessionId, string ActiveBoardId, DateTimeOffset ExpiresUtc);
public sealed record CalibrationRequest(List<BoardSettings> Boards, string ActiveBoardId);
public sealed record LightShowTestView(string Segment, DateTimeOffset EndsUtc);
public sealed record LightShowTestRequest(string Segment, LightShowSettings LightShow, AnimationSettings Animation);

public sealed class InstallationOutputService(InstallationSettingsStore settings, WLedDiscoveryService discovery,
    RgbAndSoundControlService control, DRgbService rgb, IHttpClientFactory httpFactory, TimeProvider clock,
    ILogger<InstallationOutputService> logger, IWLedFrameTransport transport) : BackgroundService
{
    private readonly ConcurrentDictionary<string, string> errors = new();
    public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>(errors);
    private long lastSendTicks;
    public DateTimeOffset? LastSendUtc => Volatile.Read(ref lastSendTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    public string? LastError { get; private set; }
    private IdleSyncSettings? appliedSync;
    private readonly CancellationTokenSource locateStop = new();
    private Task? locateTask;
    private int disposed;
    private int stopping;
    private readonly SemaphoreSlim calibrationFrames = new(1, 1);
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
        if (BoardSceneRenderer.AssignedBoards(draft).Count == 0) throw new ArgumentException("Assign at least one board before testing the light show.");
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
        var boards = JsonSerializer.SerializeToElement(request.Boards, InstallationSettingsStore.JsonOptions)
            .Deserialize<List<BoardSettings>>(InstallationSettingsStore.JsonOptions);
        var snapshot = settings.Snapshot with { Boards = boards! };
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
            foreach (var board in BoardSceneRenderer.AssignedBoards(current.Settings))
            {
                var device = Resolve(current.Settings.Devices.First(d => d.Mac == board.DeviceMac));
                try
                {
                    if (KnownOffline(device)) throw new ArgumentException("Device is offline.");
                    var pixels = board.Id == current.View.ActiveBoardId
                        ? BoardSceneRenderer.Calibration(board, device.LedCount, clock.GetElapsedTime(current.Started).TotalSeconds)
                        : new byte[device.LedCount * 3];
                    await SendAsync(device, pixels, token);
                    errors.TryRemove(device.Mac, out _);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
                { errors[device.Mac] = ex.Message; }
            }
        }
        finally { calibrationFrames.Release(); }
    }
    public DeviceSettings Resolve(DeviceSettings saved)
    {
        var found = discovery.Devices.FirstOrDefault(d => d.Mac == saved.Mac && d.IsOnline);
        return found?.Info?.Leds is { Count: > 0 and <= 65535 } leds && found.Info.Udpport is > 0 and <= 65535
            ? saved with { Address = found.Address.ToString(), HttpPort = found.Port, LedCount = leds.Count, UdpPort = found.Info.Udpport } : saved;
    }
    private bool KnownOffline(DeviceSettings d) => discovery.Devices.Any(found => found.Mac == d.Mac && !found.IsOnline);
    public static IEnumerable<byte[]> Packets(byte[] pixels)
    {
        // DNRGB permits larger strips without IP fragmentation: <= 489 pixels per packet.
        for (var start = 0; start < pixels.Length / 3; start += 489)
        {
            var length = Math.Min(489, pixels.Length / 3 - start) * 3;
            var packet = new byte[length + 4];
            packet[0] = 4; packet[1] = 1; packet[2] = (byte)(start >> 8); packet[3] = (byte)start;
            pixels.AsSpan(start * 3, length).CopyTo(packet.AsSpan(4));
            yield return packet;
        }
    }
    /// <summary>Sends one frame of the game scene to every assigned board that is not known to be offline.</summary>
    private async Task SendSceneAsync(InstallationSettings source, GamePhase phase, double elapsed, double preparation, CancellationToken token)
    {
        var scene = source with { Boards = BoardSceneRenderer.AssignedBoards(source) };
        foreach (var board in scene.Boards)
        {
            var saved = source.Devices.First(d => d.Mac == board.DeviceMac);
            if (KnownOffline(saved)) continue;
            var device = Resolve(saved);
            try
            {
                if (board.Edges.Any(e => (long)e.Start + e.Count > device.LedCount))
                    throw new ArgumentException("LED count changed; update this board's edge mapping.");
                await SendAsync(device, BoardSceneRenderer.Render(scene, board, device.LedCount, phase, elapsed, preparation), token);
                errors.TryRemove(device.Mac, out _);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
            { errors[device.Mac] = ex.Message; }
        }
    }
    private async Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        await transport.SendAsync(device, pixels, token);
        Volatile.Write(ref lastSendTicks, clock.GetUtcNow().UtcTicks);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasActive = false;
        try { await control.ConfigureAsync(() => ApplyIdleSyncAsync(stoppingToken)); }
        catch (InvalidOperationException) { /* Output already belongs to an operator. */ }
        Volatile.Write(ref ready, 1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = settings.Read();
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
                        foreach (var d in snapshot.Devices.Where(saved => saved.Selected).Where(saved => !KnownOffline(saved)).Select(Resolve))
                        {
                            try
                            {
                                if (snapshot.Boards.FirstOrDefault(b => b.DeviceMac == d.Mac) is { } board && board.Edges.Any(e => (long)e.Start + e.Count > d.LedCount))
                                    throw new ArgumentException("LED count changed; update this board's edge mapping.");
                                await SendAsync(d, BoardSceneRenderer.Resample(manual, d.LedCount), stoppingToken);
                                errors.TryRemove(d.Mac, out _);
                            }
                            catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
                            { errors[d.Mac] = ex.Message; }
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1d / (test?.Settings ?? snapshot).Animation.FramesPerSecond), clock, stoppingToken);
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
        Volatile.Write(ref ready, 0);
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
            var d = Resolve(device);
            if (KnownOffline(d)) throw new ArgumentException("Device is offline.");
            var started = clock.GetTimestamp();
            while (clock.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                var level = (byte)(30 + 150 * (Math.Sin(clock.GetElapsedTime(started).TotalSeconds * Math.PI * 2) + 1) / 2);
                var pixels = Enumerable.Repeat(level, d.LedCount * 3).ToArray();
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
    public async Task ApplyIdleSyncAsync(CancellationToken token = default, bool configure = true)
    {
        var s = settings.Snapshot;
        var oldMembers = appliedSync is { Enabled: true } old ? old.Followers.Prepend(old.MasterMac!).ToArray() : [];
        var newMembers = s.IdleSync.Enabled ? s.IdleSync.Followers.Prepend(s.IdleSync.MasterMac!).ToArray() : [];
        if (configure)
            foreach (var removed in oldMembers.Except(newMembers))
            {
                var saved = s.Devices.FirstOrDefault(d => d.Mac == removed);
                if (saved is null) continue;
                var d = Resolve(saved);
                try
                {
                    using var client = httpFactory.CreateClient("wled");
                    using var config = await client.PostAsJsonAsync($"http://{d.Address}:{d.HttpPort}/json/cfg", new
                    { @if = new { sync = new { send = new { en = false, dir = false, grp = 0 }, recv = new { grp = 0 } } } }, token);
                    config.EnsureSuccessStatusCode();
                    using var response = await client.PostAsJsonAsync($"http://{d.Address}:{d.HttpPort}/json/state", new { udpn = new { send = false, recv = false } }, token);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                { if (token.IsCancellationRequested) throw; errors[removed] = "Previous idle sync member could not be disabled."; }
            }
        appliedSync = s.IdleSync;
        if (!s.IdleSync.Enabled) return;
        var mask = 1 << (s.IdleSync.Group - 1);
        var masterDevice = Resolve(s.Devices.First(d => d.Mac == s.IdleSync.MasterMac));
        var idleState = new Dictionary<string, object?> { ["live"] = false };
        var masterRead = false;
        try
        {
            using var client = httpFactory.CreateClient("wled");
            using var response = await client.GetAsync($"http://{masterDevice.Address}:{masterDevice.HttpPort}/json/state", token);
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            if (state.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid WLED state.");
            foreach (var key in new[] { "on", "bri" }.Where(k => state.TryGetProperty(k, out _))) idleState[key] = state.GetProperty(key).Clone();
            if (state.TryGetProperty("seg", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                var main = state.TryGetProperty("mainseg", out var id) ? id.GetInt32() : 0;
                var segment = segments.EnumerateArray().FirstOrDefault(seg => seg.TryGetProperty("id", out var index) && index.GetInt32() == main);
                if (segment.ValueKind == JsonValueKind.Object)
                {
                    var effect = new Dictionary<string, object?>();
                    foreach (var key in new[] { "fx", "sx", "ix", "pal", "col", "c1", "c2", "c3", "o1", "o2", "o3" }.Where(k => segment.TryGetProperty(k, out _)))
                        effect[key] = segment.GetProperty(key).Clone();
                    idleState["seg"] = effect;
                }
            }
            masterRead = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        { if (token.IsCancellationRequested) throw; errors[masterDevice.Mac] = "Master idle state could not be read."; }
        var members = s.IdleSync.Followers.Prepend(s.IdleSync.MasterMac!);
        foreach (var mac in members)
        {
            var saved = s.Devices.First(d => d.Mac == mac);
            if (KnownOffline(saved)) continue;
            var d = Resolve(saved);
            var master = mac == s.IdleSync.MasterMac;
            try
            {
                using var client = httpFactory.CreateClient("wled");
                // Persist native sync behavior; master transmits, followers only receive.
                if (configure)
                {
                    using var config = await client.PostAsJsonAsync($"http://{d.Address}:{d.HttpPort}/json/cfg", new
                    {
                        @if = new { sync = new { send = new { en = master, dir = master, grp = master ? mask : 0 }, recv = new { grp = master ? 0 : mask, bri = true, col = true, fx = true, pal = true } } }
                    }, token);
                    config.EnsureSuccessStatusCode();
                }
                var payload = master ? new Dictionary<string, object?> { ["live"] = false } : new Dictionary<string, object?>(idleState);
                payload["udpn"] = new { send = master, sgrp = master ? mask : 0, rgrp = master ? 0 : mask };
                using var state = await client.PostAsJsonAsync($"http://{d.Address}:{d.HttpPort}/json/state", payload, token);
                state.EnsureSuccessStatusCode();
                if (!master || masterRead) errors.TryRemove(mac, out _);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { if (token.IsCancellationRequested) throw; errors[mac] = "Idle sync could not be applied."; }
        }
        // WLED emits subsequent native effect/playlist changes through its notifier.
        // Mirroring current parameters gets followers into the right state immediately
        // without toggling the master's power or restarting its playlist.
    }
}
