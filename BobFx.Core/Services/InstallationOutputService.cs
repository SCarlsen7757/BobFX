using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;

namespace BobFx.Core.Services;

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
                var active = rgb.IsUdpActive;
                if (wasActive && !active)
                {
                    try { await control.ConfigureAsync(() => ApplyIdleSyncAsync(stoppingToken, false)); }
                    catch (InvalidOperationException) { /* A new game acquired output first. */ }
                }
                wasActive = active;
                if (active)
                {
                    var scene = control.GetSceneFrame();
                    var phase = scene.Phase;
                    var elapsed = scene.Elapsed;
                    var manual = phase == GamePhase.Idle ? rgb.GetSnapshot() : null;
                    foreach (var saved in snapshot.Devices.Where(d => d.Selected))
                    {
                        if (KnownOffline(saved)) continue;
                        var d = Resolve(saved);
                        try
                        {
                            var board = snapshot.Boards.FirstOrDefault(b => b.DeviceMac == d.Mac);
                            if (board is not null && board.Edges.Any(e => (long)e.Start + e.Count > d.LedCount))
                                throw new ArgumentException("LED count changed; update this board's edge mapping.");
                            var pixels = board is not null && phase != GamePhase.Idle
                                ? BoardSceneRenderer.Render(snapshot, board, d.LedCount, phase, elapsed, scene.Preparation)
                                : BoardSceneRenderer.Resample(manual ?? rgb.GetSnapshot(), d.LedCount);
                            await SendAsync(d, pixels, stoppingToken);
                            errors.TryRemove(d.Mac, out _);
                        }
                        catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
                        { errors[d.Mac] = ex.Message; }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1d / snapshot.Animation.FramesPerSecond), clock, stoppingToken);
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
        await locateStop.CancelAsync();
        if (locateTask is { } task)
            try { await task; }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { logger.LogDebug("Locate completed before shutdown with a cancellation or device failure"); }
        await base.StopAsync(cancellationToken);
    }
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
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
            foreach (var key in new[] { "on", "bri" }) if (state.TryGetProperty(key, out var value)) idleState[key] = value.Clone();
            if (state.TryGetProperty("seg", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                var main = state.TryGetProperty("mainseg", out var id) ? id.GetInt32() : 0;
                var segment = segments.EnumerateArray().FirstOrDefault(seg => seg.TryGetProperty("id", out var index) && index.GetInt32() == main);
                if (segment.ValueKind == JsonValueKind.Object)
                {
                    var effect = new Dictionary<string, object?>();
                    foreach (var key in new[] { "fx", "sx", "ix", "pal", "col", "c1", "c2", "c3", "o1", "o2", "o3" })
                        if (segment.TryGetProperty(key, out var value)) effect[key] = value.Clone();
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
