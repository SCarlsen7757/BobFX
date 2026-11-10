using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class InstallationTests
{
    private static DeviceSettings Device(string mac = "001122334455") => new(mac, "Board", "192.168.1.20", 80, 16, 21324);
    private static BoardSettings Board(string mac = "001122334455", string id = "a") => new() { Id = id, Name = id, DeviceMac = mac,
        Edges = [new(0, 4), new(4, 4), new(8, 4), new(12, 4)] };
    private static InstallationSettings Installation() => new() { Devices = [Device()], Boards = [Board()] };
    private static InstallationSettingsStore Store(string path) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:DataPath"] = path }).Build());

    [Fact]
    public async Task UnassignedSlotsPersistAndOnlyAssignedDevicesMustBeUnique()
    {
        var path = Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N"));
        var store = Store(path);
        var first = new BoardSettings { Name = "Waiting one" };
        var second = new BoardSettings { Name = "Waiting two" };
        await store.SaveAsync(Installation() with { Boards = [first, Board(), second] });
        var saved = Store(path).Snapshot;
        Assert.Equal(3, saved.Boards.Count);
        Assert.Null(saved.Boards[0].DeviceMac); Assert.Empty(saved.Boards[0].Edges);
        Assert.Equal("001122334455", saved.Boards[1].DeviceMac);
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { Boards = [first, first] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { Boards = [first with { DeviceMac = "001122334455" }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { Boards = [first with { DeviceMac = "" }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { Boards = [Board(), Board(id: "duplicate")] }));
    }

    [Fact]
    public void UnassignedSlotsDoNotIntroduceScannerOrDotGaps()
    {
        var a = Board(); var b = Board("aabbccddeeff", "b");
        var compact = Installation() with { Devices = [Device(), Device("aabbccddeeff")], Boards = [a, b] };
        var withGaps = compact with { Boards = [new(), a, new(), b, new()] };
        foreach (var phase in new[] { GamePhase.PreCountdown, GamePhase.Running, GamePhase.Ending })
        foreach (var time in new[] { .2, 1.5, 2.1, 3.5 })
        foreach (var board in new[] { a, b })
            Assert.Equal(BoardSceneRenderer.Render(compact, board, 16, phase, time, 5), BoardSceneRenderer.Render(withGaps, board, 16, phase, time, 5));
        Assert.Equal(BoardSceneRenderer.DotPath(compact.Boards), BoardSceneRenderer.DotPath(withGaps.Boards));
        Assert.All(BoardSceneRenderer.Render(withGaps, withGaps.Boards[0], 16, GamePhase.Running, 1, 5), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CalibrationColorsFollowVisualEdgesAndMarkerTravelsClockwise(int rotation)
    {
        var board = Board() with { Rotation = rotation, Edges = [new(0, 4), new(4, 4), new(12, 4, true), new(8, 4, true)] };
        byte[][] colors = [[0, 96, 160], [0, 128, 64], [160, 96, 0], [144, 0, 96]];
        for (var side = 0; side < 4; side++)
        {
            var frame = BoardSceneRenderer.Calibration(board, 16, side);
            var marker = BoardSceneRenderer.EdgePixels(board, side)[0];
            Assert.Equal(new byte[] { 220, 220, 220 }, frame[(marker * 3)..(marker * 3 + 3)]);
            for (var edge = 0; edge < 4; edge++)
            foreach (var pixel in BoardSceneRenderer.EdgePixels(board, edge).Where(p => p != marker))
                Assert.Equal(colors[edge], frame[(pixel * 3)..(pixel * 3 + 3)]);
        }
        Assert.Throws<ArgumentException>(() => BoardSceneRenderer.Calibration(board, 15, 0));
    }

    [Fact]
    public async Task CalibrationPreviewsDraftOwnsOutputRenewsExpiresAndReleasesOnShutdown()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N")));
        var saved = Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }],
            Boards = [Board(), new(), Board("aabbccddeeff", "b")] };
        await store.SaveAsync(saved);
        var clock = new FakeTimeProvider();
        var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var control = new RgbAndSoundControlService(timer, rgb, new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance, store);
        var clients = new DummyClients();
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance, options, clients, new EmptyDiscovery());
        var transport = new RecordingTransport();
        using var output = new InstallationOutputService(store, discovery, control, rgb, clients, clock, NullLogger<InstallationOutputService>.Instance, transport);
        await output.StartAsync(CancellationToken.None);
        await Eventually(() => output.IsReady);
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartCalibrationAsync(new(saved.Boards, saved.Boards[1].Id)));
        Assert.False(control.IsCalibrating);
        var initialDraft = saved.Boards.Select(b => b with { Edges = b.Edges.ToList() }).ToList();
        await output.StartCalibrationAsync(new(initialDraft, "a"));
        initialDraft[0].Edges[0] = new(60000, 1); // Session must own a validated copy of the draft.
        var id = output.Calibration!.SessionId;
        Assert.True(control.IsCalibrating);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartGameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ConfigureAsync(() => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.LocateAsync("001122334455"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "b")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StopCalibrationAsync("other-session"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.UpdateCalibrationAsync("other-session", new(saved.Boards, "b")));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => transport.Frames.Count >= 2);
        Assert.Contains(transport.Frames, f => f.Mac == "001122334455" && f.Pixels.Any(p => p != 0));
        Assert.Contains(transport.Frames, f => f.Mac == "aabbccddeeff" && f.Pixels.All(p => p == 0));
        var reordered = new List<BoardSettings> { saved.Boards[2] with { Rotation = 90 }, saved.Boards[0], saved.Boards[1] };
        clock.Advance(TimeSpan.FromSeconds(9));
        await output.UpdateCalibrationAsync(id, new(reordered, "b"));
        Assert.Equal("b", output.Calibration!.ActiveBoardId);
        Assert.Equal("a", store.Snapshot.Boards[0].Id);
        Assert.Equal(0, store.Snapshot.Boards[2].Rotation);
        await Assert.ThrowsAsync<ArgumentException>(() => output.UpdateCalibrationAsync(id, new([saved.Boards[0]], "a")));
        transport.FailMac = "001122334455";
        var count = transport.Frames.Count;
        clock.Advance(TimeSpan.FromSeconds(6));
        await Eventually(() => transport.Frames.Count > count);
        Assert.True(control.IsCalibrating);
        Assert.Contains("001122334455", output.Errors.Keys);
        Assert.Contains(transport.Frames, f => f.Mac == "aabbccddeeff" && f.Pixels.Any(p => p != 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => output.Calibration is null);
        Assert.False(control.IsCalibrating);
        await control.ConfigureAsync(() => Task.CompletedTask);
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "a")));
        await control.StopManualEffectAsync();
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "a")));
        await control.StopGameAsync();
        await output.StartCalibrationAsync(new(saved.Boards, "a"));
        await output.StopAsync(CancellationToken.None);
        Assert.Null(output.Calibration); Assert.False(control.IsCalibrating);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "a")));
    }

    [Fact]
    public async Task GameOutputUsesOnlyAssignedBoardsWhileManualOutputStillUsesAllSelectedDevices()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }], Boards = [new(), Board(), new()] });
        var clock = new FakeTimeProvider();
        var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var control = new RgbAndSoundControlService(timer, rgb, new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance, store);
        var clients = new DummyClients();
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance, options, clients, new EmptyDiscovery());
        var transport = new RecordingTransport();
        using var output = new InstallationOutputService(store, discovery, control, rgb, clients, clock, NullLogger<InstallationOutputService>.Instance, transport);
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        await output.StartAsync(CancellationToken.None);
        await Eventually(() => transport.Sent.Contains("001122334455"));
        Assert.DoesNotContain("aabbccddeeff", transport.Sent);
        await control.StopGameAsync();
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => transport.Sent.Contains("aabbccddeeff"));
        await output.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SaveReloadRejectsInvalidAndDoesNotShareMutableCollections()
    {
        var path = Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N"));
        var store = Store(path);
        var settings = Installation();
        await store.SaveAsync(settings);
        settings.Boards.Clear();
        Assert.Single(store.Snapshot.Boards);
        var copy = store.Snapshot; copy.Devices.Clear();
        Assert.Single(store.Snapshot.Devices);
        var before = await File.ReadAllTextAsync(Path.Combine(path, "settings.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(store.Snapshot with { Version = 2 }));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(path, "settings.json")));
        Assert.Single(Store(path).Snapshot.Boards);
        Assert.Empty(Directory.GetFiles(path, "*.tmp"));
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{broken");
        Assert.Throws<JsonException>(() => Store(path));
    }
    [Fact]
    public async Task ConcurrentSectionUpdatesPreserveBothSections()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N")));
        await Task.WhenAll(store.UpdateAsync(s => s with { Devices = [Device()] }),
            store.UpdateAsync(s => s with { Animation = s.Animation with { FramesPerSecond = 20 } }));
        Assert.Single(store.Snapshot.Devices); Assert.Equal(20, store.Snapshot.Animation.FramesPerSecond);
    }
    [Fact]
    public void EdgeMappingRejectsOverlapBoundsAndDuplicateAssignment()
    {
        var original = Installation();
        var board = original.Boards[0];
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Boards = [board with { Edges = [new(0, 4), new(3, 4), new(8, 4), new(12, 4)] }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Boards = [board with { Edges = [new(0, 4), new(4, 4), new(8, 4), new(15, 4)] }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Boards = [board, board with { Id = "b" }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Devices = [Device() with { Selected = false }] }));
        Assert.Equal(new[] { 3, 2, 1, 0 }, BoardSceneRenderer.EdgePixels(board with { Edges = [new(0, 4, true), new(4, 4), new(8, 4), new(12, 4)] }, 0));
        Assert.Equal(new[] { 12, 13, 14, 15 }, BoardSceneRenderer.EdgePixels(board with { Rotation = 90 }, 0));
    }
    [Fact]
    public void PreparationLightsPlayerEdgesThenRemainingEdgesAndGo()
    {
        var settings = Installation(); var board = settings.Boards[0];
        var player = BoardSceneRenderer.Render(settings, board, 16, GamePhase.PreCountdown, 3.5, 5);
        Assert.All(Enumerable.Range(0, 4).Concat(Enumerable.Range(8, 4)), i => Assert.Equal(255, player[i * 3 + 2]));
        Assert.All(Enumerable.Range(4, 4).Concat(Enumerable.Range(12, 4)), i => Assert.Equal(0, player[i * 3 + 2]));
        var all = BoardSceneRenderer.Render(settings, board, 16, GamePhase.PreCountdown, 4.5, 5);
        Assert.Equal(16, all.Count(b => b == 255));
        var go = BoardSceneRenderer.Render(settings, board, 16, GamePhase.Running, 0, 5);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(new byte[] { 0, 255, 0 }, go[(i * 3)..(i * 3 + 3)]));
    }
    [Fact]
    public void ReorderedScannerAndSingleDotUseConfiguredRow()
    {
        var a = Board(); var b = Board("aabbccddeeff", "b");
        var settings = Installation() with { Devices = [Device(), Device("aabbccddeeff")], Boards = [a, b] };
        // Early scanner reaches only the first board.
        Assert.Contains((byte)255, BoardSceneRenderer.Render(settings, a, 16, GamePhase.PreCountdown, 0.2, 5));
        Assert.All(BoardSceneRenderer.Render(settings, b, 16, GamePhase.PreCountdown, 0.2, 5), value => Assert.Equal(0, value));
        settings = settings with { Boards = [b, a] };
        Assert.Contains((byte)255, BoardSceneRenderer.Render(settings, b, 16, GamePhase.PreCountdown, 0.2, 5));
        foreach (var time in new[] { 2d, 2.03, 2.12, 2.9, 4.9 })
        {
            var frames = settings.Boards.SelectMany(board => BoardSceneRenderer.Render(settings, board, 16, GamePhase.Ending, time, 5)).ToArray();
            Assert.Equal(1, frames.Count(value => value == 255));
        }
        var path = BoardSceneRenderer.DotPath(settings.Boards);
        Assert.Equal(32, path.Count); Assert.Equal(32, path.Distinct().Count());
        Assert.Equal("b", path[0].BoardId);
        Assert.Equal(16, BoardSceneRenderer.DotPath([a]).Count);
    }
    [Fact]
    public void LargeStripsProduceBoundedPacketsWithOffsets()
    {
        var data = Enumerable.Range(0, 1000 * 3).Select(i => (byte)(i % 256)).ToArray();
        var packets = InstallationOutputService.Packets(data).ToArray();
        Assert.Equal(3, packets.Length);
        Assert.All(packets, packet => Assert.InRange(packet.Length, 7, 1471));
        Assert.Equal(new byte[] { 4, 1, 1, 233 }, packets[1][..4]);
        Assert.Equal(data, packets.SelectMany(p => p.Skip(4)).ToArray());
    }
    [Fact]
    public void BusyBarRunningScreenContainsNoTimerAndInputIgnoresBackReleasesUnknownAndMalformedFrames()
    {
        var running = JsonSerializer.Serialize(BusyBarService.BuildDisplay(GamePhase.Running, 5, 5, new()));
        Assert.DoesNotContain("timer", running); Assert.DoesNotContain("countdown", running); Assert.DoesNotContain("text", running);
        Assert.Contains("bobfx-running-b1-", running);
        Assert.Contains("bobfx-running-b0-", JsonSerializer.Serialize(BusyBarService.BuildDisplay(GamePhase.Running, 5, 5, new(), BusyBarAnimFormat.Bicycle0)));
        Assert.Contains("animation", running);
        // State.updates(2) -> StateUpdate.input(11) -> InputEvent.button_event(1).
        Assert.Equal(new[] { 2 }, BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x0a, 2, 0x08, 2]));
        // Pressing the wheel is OK, button 0: sent explicitly, or omitted as the protobuf default.
        Assert.Equal(new[] { 0 }, BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x0a, 2, 0x08, 0]));
        Assert.Equal(new[] { 0 }, BusyBarInputDecoder.Presses([0x12, 4, 0x5a, 2, 0x0a, 0]));
        Assert.Empty(BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x0a, 2, 0x10, 1])); // Wheel released.
        Assert.Empty(BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x0a, 2, 0x08, 1])); // BACK belongs to the bar.
        Assert.Empty(BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x1a, 2, 0x08, 1])); // Turning the wheel.
        Assert.Empty(BusyBarInputDecoder.Presses([0x12, 8, 0x5a, 6, 0x0a, 4, 0x08, 2, 0x10, 1]));
        Assert.Empty(BusyBarInputDecoder.Presses([0x12, 6, 0x5a, 4, 0x0a, 2, 0x08, 99]));
        Assert.Throws<InvalidDataException>(() => BusyBarInputDecoder.Presses([0x12, 99]));
    }
    [Fact]
    public void BusyBarPreparationPlaysTheCountdownClipAndIdleIsBranded()
    {
        // The seconds are baked into the countdown clip, so no text element is ever sent.
        var display = JsonSerializer.Serialize(BusyBarService.BuildDisplay(GamePhase.PreCountdown, 1.2, 5, new()));
        Assert.Contains("bobfx-countdown5-", display); Assert.Contains("from1", display); Assert.DoesNotContain("text", display);
        var idle = JsonSerializer.Serialize(BusyBarService.BuildDisplay(GamePhase.Idle, 2, 5, new()));
        Assert.Contains("bobfx-idle-", idle);
        var go = JsonSerializer.Serialize(BusyBarService.BuildDisplay(GamePhase.Running, 0.2, 5, new()));
        Assert.Contains("bobfx-go-", go);
    }
    [Fact]
    public async Task SettingsSavedWithTheRemovedBusyBarTimerOptionStillLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), """
            { "version": 1, "controllers": [ { "id": "remote", "name": "Family remote", "address": "192.168.1.30", "port": 80,
              "enabled": true, "showTimer": true, "remoteEnabled": true, "secretName": "busybar_token" } ] }
            """);
        var store = Store(path);
        var bar = Assert.Single(store.Snapshot.Controllers);
        Assert.True(bar.Enabled); Assert.True(bar.RemoteEnabled); Assert.Equal("Family remote", bar.Name);
        await store.SaveAsync(store.Snapshot);
        Assert.DoesNotContain("showTimer", await File.ReadAllTextAsync(Path.Combine(path, "settings.json")));
    }
    [Fact]
    public async Task LocateOwnsOutputAndRejectsGameManualAndOtherLocate()
    {
        var clock = new FakeTimeProvider();
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var control = new RgbAndSoundControlService(timer, rgb, new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locating = control.LocateAsync(() => release.Task);
        Assert.True(control.IsLocating);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartGameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.LocateAsync(() => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid)));
        release.SetResult(); await locating;
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        Assert.False(timer.IsRunning); Assert.Equal(GamePhase.PreCountdown, control.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.LocateAsync(() => Task.CompletedTask));
        await control.StartAsync(CancellationToken.None);
        await Eventually(() => control.IsReady);
        clock.Advance(CountdownService.PreCountdownDuration);
        await Eventually(() => control.Phase == GamePhase.Running);
        Assert.True(timer.IsRunning); Assert.Equal(TimeSpan.FromSeconds(60), timer.Remaining);
        await control.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task DeviceFailureDoesNotPreventOtherFramesAndLocateUsesOnlyTarget()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }], Boards = [Board(), Board("aabbccddeeff", "b")] });
        var clock = new FakeTimeProvider();
        var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var control = new RgbAndSoundControlService(timer, rgb, new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance, store);
        var clients = new DummyClients();
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance, options, clients, new EmptyDiscovery());
        var transport = new RecordingTransport();
        using var output = new InstallationOutputService(store, discovery, control, rgb, clients, clock, NullLogger<InstallationOutputService>.Instance, transport);
        using var locateCancel = new CancellationTokenSource();
        var locating = output.LocateAsync("aabbccddeeff", locateCancel.Token);
        Assert.Single(transport.Sent); Assert.Equal("aabbccddeeff", transport.Sent.Single());
        locateCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locating);
        Assert.False(control.IsLocating);
        transport.Sent.Clear(); transport.FailMac = "001122334455";
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(60), deviation: TimeSpan.Zero);
        await output.StartAsync(CancellationToken.None);
        await Eventually(() => transport.Sent.Contains("aabbccddeeff"));
        Assert.Contains("001122334455", output.Errors.Keys);
        await output.StopAsync(CancellationToken.None);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }
    private sealed class RecordingTransport : IWLedFrameTransport
    {
        public ConcurrentQueue<string> Sent { get; } = new();
        public ConcurrentQueue<(string Mac, byte[] Pixels)> Frames { get; } = new();
        public string? FailMac { get; set; }
        public Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
        {
            if (device.Mac == FailMac) throw new IOException("Test device offline.");
            Sent.Enqueue(device.Mac); Frames.Enqueue((device.Mac, pixels.ToArray())); return Task.CompletedTask;
        }
    }
    private sealed class DummyClients : IHttpClientFactory { public HttpClient CreateClient(string name) => new(new DummyHandler()); }
    [Fact]
    public async Task IdleSyncUsesNativeConfigKeysAndMirrorsEffectsWithoutCopyingBoundsOrTurningOnMaster()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N")));
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff")], Boards = [],
            IdleSync = new IdleSyncSettings { Enabled = true, MasterMac = "001122334455", Followers = ["aabbccddeeff"], Group = 2 } });
        var clock = new FakeTimeProvider(); var options = Options.Create(new WLedOptions());
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, options, new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var control = new RgbAndSoundControlService(timer, rgb, new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance, store);
        var clients = new SyncClients();
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance, options, clients, new EmptyDiscovery());
        using var output = new InstallationOutputService(store, discovery, control, rgb, clients, clock, NullLogger<InstallationOutputService>.Instance, new RecordingTransport());
        await output.ApplyIdleSyncAsync();
        var config = clients.Requests.First(r => r.Path == "/json/cfg").Body;
        Assert.True(config.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("en").GetBoolean());
        Assert.Equal(2, config.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("grp").GetInt32());
        var states = clients.Requests.Where(r => r.Path == "/json/state").ToArray();
        Assert.False(states[0].Body.TryGetProperty("on", out _)); // Never force the master on.
        Assert.False(states[1].Body.GetProperty("on").GetBoolean());
        Assert.Equal(7, states[1].Body.GetProperty("seg").GetProperty("fx").GetInt32());
        Assert.False(states[1].Body.GetProperty("seg").TryGetProperty("start", out _));
        clients.Requests.Clear();
        await output.ApplyIdleSyncAsync(configure: false);
        Assert.DoesNotContain(clients.Requests, r => r.Path == "/json/cfg"); // Avoid flash writes every game.
        await store.UpdateAsync(s => s with { IdleSync = s.IdleSync with { Enabled = false } });
        clients.Requests.Clear(); await output.ApplyIdleSyncAsync();
        Assert.All(clients.Requests.Where(r => r.Path == "/json/cfg"), r => Assert.False(r.Body.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("en").GetBoolean()));
    }
    private sealed class SyncClients : IHttpClientFactory
    {
        public List<(string Path, JsonElement Body)> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new SyncHandler(Requests));
    }
    private sealed class SyncHandler(List<(string Path, JsonElement Body)> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("""{"on":false,"bri":100,"mainseg":0,"seg":[{"id":0,"start":0,"stop":16,"fx":7,"col":[[0,0,255]]}]}""", System.Text.Encoding.UTF8, "application/json") };
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadFromJsonAsync<JsonElement>(token)));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true }) };
        }
    }
    private sealed class DummyHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) }); }
    private sealed class EmptyDiscovery : IWLedDiscoverySource
    { public Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<WLedAdvertisement>>([]); }
}
