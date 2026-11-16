using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BobFx.Core.Hubs;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.SignalR;

namespace BobFx.Core.Services;

public sealed record Versioned<T>(string ServerInstanceId, long Revision, DateTimeOffset ServerUtc, T Value);
public sealed record GameView(string Phase, long RunId, bool Running, bool PreCountdownRunning,
    int RemainingSeconds, int PreRemainingSeconds, DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc, bool Opening, bool GameOver);
public sealed record EffectView(string CurrentEffect, double Speed, int LedCount, bool Streaming, string? Error);
public sealed record DeviceView(string Mac, string Address, int Port, string Name, string HostName,
    string Version, DateTime LastSeen, bool IsOnline, string WebUrl);
public sealed record DiscoveryView(bool Scanning, string? Error, DateTimeOffset? LastScanUtc, DeviceView[] Devices);
public sealed record InstallationView(InstallationSettings Settings, bool Locating, IReadOnlyDictionary<string, string> OutputErrors,
    IReadOnlyDictionary<string, BusyBarStatus> Controllers, Dictionary<string, bool> PasswordsSaved, CalibrationView? Calibration,
    LightShowTestView? LightShowTest);
public sealed record UiState(Versioned<GameView> Game, Versioned<EffectView> Effect,
    Versioned<DiscoveryView> Discovery, Versioned<InstallationView> Installation, CountdownOptions Defaults);
public sealed record SoundEvent(string EventId, long RunId, string Key);

/// <summary>One publisher samples shared services, independently of browser count.</summary>
public sealed class UiStateService(
    CountdownService countdown, RgbAndSoundControlService control, DRgbService rgb,
    WLedDiscoveryService discovery, SoundService sounds, TimeProvider clock,
    InstallationSettingsStore settings, InstallationOutputService output, BusyBarService busyBars, ControllerCredentials credentials,
    IHubContext<UiHub> hub, ILogger<UiStateService> logger) : BackgroundService
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly Lock sync = new();
    private Versioned<GameView>? game;
    private Versioned<EffectView>? effect;
    private Versioned<DiscoveryView>? devices;
    private IReadOnlyList<WLedDevice>? discovered;
    private Versioned<InstallationView>? installation;
    private readonly Channel<SoundEvent> audio = Channel.CreateBounded<SoundEvent>(new BoundedChannelOptions(32)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> previewClients = new();
    private int previewRefresh;
    /// <summary>The browser has already joined the preview group, so the next tick's frame reaches it.</summary>
    public void SubscribePreview(string connectionId) { previewClients.TryAdd(connectionId, 0); Volatile.Write(ref previewRefresh, 1); }
    public void UnsubscribePreview(string connectionId) => previewClients.TryRemove(connectionId, out _);

    public UiState GetState()
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            var state = countdown.Snapshot;
            var nextGame = new GameView(control.Phase.ToString(), state.RunId, state.IsRunning, state.IsPreCountdownRunning,
                (int)Math.Ceiling(state.Remaining.TotalSeconds), (int)Math.Ceiling(state.PreCountdownRemaining.TotalSeconds),
                state.StartedUtc, state.EndedUtc,
                state.IsRunning && state.StartedUtc is { } start && now < start.AddSeconds(2.5),
                !state.IsRunning && !state.IsPreCountdownRunning && state.EndedUtc is { } end && now < end.AddSeconds(15));
            if (game?.Value != nextGame) game = new(instanceId, (game?.Revision ?? 0) + 1, now, nextGame);
            var status = rgb.GetStatus();
            var nextEffect = new EffectView(status.Effect.ToString(), status.Speed.TotalMilliseconds, status.LedCount, status.Streaming, status.LastError);
            if (effect?.Value != nextEffect) effect = new(instanceId, (effect?.Revision ?? 0) + 1, now, nextEffect);
            // The device list is the same instance until a device changes, so its views are only rebuilt then.
            var found = discovery.Devices;
            var (scanning, scanError, lastScan) = (discovery.IsScanning, discovery.LastError, discovery.LastScanUtc);
            if (devices is null || !ReferenceEquals(found, discovered) || devices.Value.Scanning != scanning || devices.Value.Error != scanError || devices.Value.LastScanUtc != lastScan)
            {
                discovered = found;
                devices = new(instanceId, (devices?.Revision ?? 0) + 1, now, new DiscoveryView(scanning, scanError, lastScan, found.Select(ToView).ToArray()));
            }
            var saved = settings.Snapshot;
            var nextInstallation = new InstallationView(saved, control.IsLocating, output.Errors, busyBars.Statuses,
                saved.Controllers.ToDictionary(c => c.Id, c => credentials.HasPassword(c.Id)), output.Calibration, output.LightShowTest);
            if (installation is null || !Same(installation.Value, nextInstallation))
                installation = new(instanceId, (installation?.Revision ?? 0) + 1, now, nextInstallation);
            return new(game!, effect!, devices!, installation!,
                new CountdownOptions { DurationSeconds = (int)countdown.CountdownDuration.TotalSeconds, DeviationSeconds = (int)countdown.CountdownDeviation.TotalSeconds });
        }
    }

    /// <summary>Whether a browser would see the same installation. Saved settings are compared by instance, which
    /// a save replaces; the rest are small and compared by value.</summary>
    private static bool Same(InstallationView a, InstallationView b) =>
        ReferenceEquals(a.Settings, b.Settings) && a.Locating == b.Locating && a.Calibration == b.Calibration && a.LightShowTest == b.LightShowTest &&
        SameEntries(a.OutputErrors, b.OutputErrors) && SameEntries(a.Controllers, b.Controllers) && SameEntries(a.PasswordsSaved, b.PasswordsSaved);
    private static bool SameEntries<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var other) && EqualityComparer<T>.Default.Equals(entry.Value, other));

    public static DeviceView ToView(WLedDevice device) => new(device.Mac, device.Address.ToString(), device.Port,
        device.Name, device.HostName, device.Version, device.LastSeen, device.IsOnline,
        new UriBuilder("http", device.Address.ToString(), device.Port).Uri.AbsoluteUri);

    private void OnSound(string path)
    {
        if (path == "/sounds/game_over/game-over-yeah.mp3")
            audio.Writer.TryWrite(new(Guid.NewGuid().ToString("N"), countdown.Snapshot.RunId, "end"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        sounds.SoundRequested += OnSound;
        long gameRevision = 0, effectRevision = 0, discoveryRevision = 0, installationRevision = 0;
        System.Numerics.Vector3[] previewSent = [];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var state = GetState();
                    if (state.Game.Revision != gameRevision)
                    {
                        await hub.Clients.All.SendAsync("GameStateChanged", state.Game, stoppingToken);
                        gameRevision = state.Game.Revision;
                    }
                    if (state.Effect.Revision != effectRevision)
                    {
                        await hub.Clients.All.SendAsync("EffectStateChanged", state.Effect, stoppingToken);
                        effectRevision = state.Effect.Revision;
                    }
                    if (state.Discovery.Revision != discoveryRevision)
                    {
                        await hub.Clients.All.SendAsync("DevicesChanged", state.Discovery, stoppingToken);
                        discoveryRevision = state.Discovery.Revision;
                    }
                    if (state.Installation.Revision != installationRevision)
                    {
                        await hub.Clients.All.SendAsync("InstallationChanged", state.Installation, stoppingToken);
                        installationRevision = state.Installation.Revision;
                    }
                    while (audio.Reader.TryRead(out var sound))
                        await hub.Clients.All.SendAsync("SoundRequested", sound, stoppingToken);
                    if (!previewClients.IsEmpty)
                    {
                        // A strip that shows the same as last time is not sent again, unless a browser just joined.
                        var strip = rgb.GetSnapshot();
                        if (Interlocked.Exchange(ref previewRefresh, 0) == 1 || !strip.AsSpan().SequenceEqual(previewSent))
                        {
                            await hub.Clients.Group(UiHub.PreviewGroup).SendAsync("LedPreviewChanged", strip.Select(ColorHelper.RgbToHex).ToArray(), stoppingToken);
                            previewSent = strip;
                        }
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ExceptionPolicy.IsRecoverable(ex))
                { logger.LogWarning(ex, "UI update failed; retrying on the next tick"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { logger.LogDebug("UI state updates stopped because the host is shutting down"); }
        finally { sounds.SoundRequested -= OnSound; }
    }
}
