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

namespace BobFx.Core.Tests;

using static TestData;

public sealed class InstallationTests
{
    [Fact]
    public async Task UnassignedSlotsPersistAndOnlyAssignedDevicesMustBeUnique()
    {
        var path = TestPaths.NewDataPath();
        var store = Store(path);
        var first = new BoardSettings { Name = "Waiting one" };
        var second = new BoardSettings { Name = "Waiting two" };
        await store.SaveAsync(Installation() with { Boards = [first, Board(), second] }, TestContext.Current.CancellationToken);
        var saved = Store(path).Snapshot;
        Assert.Equal(3, saved.Boards.Count);
        Assert.Null(saved.Boards[0].DeviceMac); Assert.Null(saved.Boards[0].Left); Assert.Null(saved.Boards[0].Right);
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

    [Fact]
    public void CalibrationColorsTheTwoStripsAndRunsADotFromTheNearEndOfEach()
    {
        // The right strip is wired from the far end, so its near end is its last LED.
        var board = Board() with { Right = new(8, 8, true) };
        Assert.Equal(15, BoardMapping.StripPixels(board, BoardMapping.Right)[0]);
        byte[][] colors = [[0, 96, 160], [160, 96, 0]];
        // The dot takes two seconds from the near end to the far end of a strip.
        foreach (var (time, step) in new[] { (0d, 0), (0.25, 1), (1.75, 7), (2d, 0) })
        {
            var frame = BoardSceneRenderer.Calibration(board, 16, time);
            for (var side = 0; side < 2; side++)
            {
                var pixels = BoardMapping.StripPixels(board, side);
                Assert.Equal(new byte[] { 220, 220, 220 }, frame[(pixels[step] * 3)..(pixels[step] * 3 + 3)]);
                foreach (var pixel in pixels.Where(p => p != pixels[step]))
                    Assert.Equal(colors[side], frame[(pixel * 3)..(pixel * 3 + 3)]);
            }
        }
        Assert.Throws<ArgumentException>(() => BoardSceneRenderer.Calibration(board, 15, 0));
    }

    [Fact]
    public async Task CalibrationPreviewsDraftOwnsOutputRenewsExpiresAndReleasesOnShutdown()
    {
        await using var h = new TestHarness();
        var store = h.Store;
        var saved = Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }],
            Boards = [Board(), new(), Board("aabbccddeeff", "b")] };
        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var (clock, control, transport, output) = (h.Clock, h.Control, h.Transport, h.Output);
        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => output.IsReady);
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartCalibrationAsync(new(saved.Boards, saved.Boards[1].Id)));
        Assert.False(control.IsCalibrating);
        var initialDraft = saved.Boards.ToList();
        await output.StartCalibrationAsync(new(initialDraft, "a"));
        initialDraft[0] = initialDraft[0] with { Left = new(60000, 1) }; // Session must own a validated copy of the draft.
        var id = output.Calibration!.SessionId;
        Assert.True(control.IsCalibrating);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartGameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ConfigureAsync(() => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.LocateAsync("001122334455", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "b")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StopCalibrationAsync("other-session"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.UpdateCalibrationAsync("other-session", new(saved.Boards, "b")));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => transport.Frames.Count >= 2);
        Assert.Contains(transport.Frames, f => f.Mac == "001122334455" && f.Pixels.Any(p => p != 0));
        Assert.Contains(transport.Frames, f => f.Mac == "aabbccddeeff" && f.Pixels.All(p => p == 0));
        var reordered = new List<BoardSettings> { saved.Boards[2] with { Left = new(8, 8), Right = new(0, 8) }, saved.Boards[0], saved.Boards[1] };
        clock.Advance(TimeSpan.FromSeconds(9));
        await output.UpdateCalibrationAsync(id, new(reordered, "b"));
        Assert.Equal("b", output.Calibration!.ActiveBoardId);
        Assert.Equal("a", store.Snapshot.Boards[0].Id);
        Assert.Equal(new StripSettings(0, 8), store.Snapshot.Boards[2].Left); // The swapped draft is previewed, not saved.
        await Assert.ThrowsAsync<ArgumentException>(() => output.UpdateCalibrationAsync(id, new([saved.Boards[0]], "a")));
        transport.FailMac = "001122334455";
        var count = transport.Frames.Count;
        clock.Advance(TimeSpan.FromSeconds(6));
        await Wait.Until(() => transport.Frames.Count > count);
        Assert.True(control.IsCalibrating);
        Assert.Contains("001122334455", output.Errors.Keys);
        Assert.Contains(transport.Frames, f => f.Mac == "aabbccddeeff" && f.Pixels.Any(p => p != 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => output.Calibration is null);
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
        await using var h = new TestHarness();
        var store = h.Store;
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }], Boards = [new(), Board(), new()] }, TestContext.Current.CancellationToken);
        var (clock, control, transport, output) = (h.Clock, h.Control, h.Transport, h.Output);
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => transport.Sent.Contains("001122334455"));
        Assert.DoesNotContain("aabbccddeeff", transport.Sent);
        await control.StopGameAsync();
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => transport.Sent.Contains("aabbccddeeff"));
        await output.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SaveReloadRejectsInvalidAndKeepsItsOwnLists()
    {
        var path = TestPaths.NewDataPath();
        var store = Store(path);
        // Saved settings cannot be changed through the list they were built from; reading them gives no list to change.
        List<BoardSettings> boards = [Board()];
        await store.SaveAsync(Installation() with { Boards = boards }, TestContext.Current.CancellationToken);
        boards.Clear();
        Assert.Single(store.Snapshot.Boards);
        Assert.Same(store.Snapshot, store.Snapshot);
        var before = await File.ReadAllTextAsync(Path.Combine(path, "settings.json"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(store.Snapshot with { Version = 3 }, TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(path, "settings.json"), TestContext.Current.CancellationToken));
        Assert.Single(Store(path).Snapshot.Boards);
        Assert.Empty(Directory.GetFiles(path, "*.tmp"));
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), "{broken", TestContext.Current.CancellationToken);
        Assert.Throws<JsonException>(() => Store(path));
    }
    [Fact]
    public async Task ConcurrentSectionUpdatesPreserveBothSections()
    {
        var store = Store(TestPaths.NewDataPath());
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(store.UpdateAsync(s => s with { Devices = [Device()] }, token),
            store.UpdateAsync(s => s with { Animation = s.Animation with { FramesPerSecond = 20 } }, token));
        Assert.Single(store.Snapshot.Devices); Assert.Equal(20, store.Snapshot.Animation.FramesPerSecond);
    }
    [Fact]
    public void StripMappingRejectsOverlapBoundsAndDuplicateAssignmentAndSaysWhichStrip()
    {
        var original = Installation();
        var board = original.Boards[0];
        void Rejected(BoardSettings changed, string because) => Assert.Contains(because,
            Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Boards = [changed] })).Message);
        Rejected(board with { Right = new(7, 8) }, "a: the left and right strips use the same LEDs.");
        Rejected(board with { Right = new(8, 9) }, "a: the right strip uses LEDs 8–16, but Board has 16 LEDs (0–15).");
        Rejected(board with { Left = new(0, 0) }, "a: the left strip needs a first LED of 0 or more and at least one LED.");
        Rejected(board with { Left = new(-1, 4) }, "a: the left strip needs");
        Rejected(board with { Right = null }, "a: map both the left and the right strip.");
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Boards = [board, board with { Id = "b" }] }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(original with { Devices = [Device() with { Selected = false }] }));
        // Strips may leave LEDs unused and sit anywhere on the controller.
        InstallationSettingsStore.Validate(original with { Boards = [board with { Left = new(10, 3), Right = new(2, 5, true) }] });

        // Pixels are listed from the near end, whichever end the strip is wired from.
        Assert.Equal(new[] { 7, 6, 5, 4, 3, 2, 1, 0 }, BoardMapping.StripPixels(board with { Left = new(0, 8, true) }, BoardMapping.Left));
        Assert.Equal(Enumerable.Range(8, 8), BoardMapping.StripPixels(board, BoardMapping.Right));
        Assert.Empty(BoardMapping.StripPixels(new BoardSettings(), BoardMapping.Left));
        Assert.True(BoardMapping.Fits(board, 16)); Assert.False(BoardMapping.Fits(board, 15));

        // A standard board has 51 LEDs a side: the first half of the controller on the left, the rest on the right.
        Assert.Equal((new StripSettings(0, 51), new StripSettings(51, 51)), BoardMapping.Default(BoardMapping.LedsPerBoard));
        Assert.Equal((new StripSettings(0, 8), new StripSettings(8, 8)), BoardMapping.Default(17));
        Assert.Null(BoardMapping.Default(1));
    }
    [Fact]
    public async Task VersionOneSettingsWithFourEdgesMigrateToTheDefaultTwoStrips()
    {
        var path = TestPaths.NewDataPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), """
            { "version": 1,
              "devices": [ { "mac": "001122334455", "name": "Left table", "address": "192.168.1.20", "httpPort": 80, "ledCount": 102, "udpPort": 21324, "selected": true } ],
              "boards": [
                { "id": "a", "name": "A", "deviceMac": "001122334455", "rotation": 90,
                  "edges": [ { "start": 0, "count": 25 }, { "start": 25, "count": 25 }, { "start": 75, "count": 25, "reversed": true }, { "start": 50, "count": 25, "reversed": true } ] },
                { "id": "b", "name": "B", "deviceMac": null, "rotation": 0, "edges": [] } ] }
            """, TestContext.Current.CancellationToken);
        var store = Store(path);
        var saved = store.Snapshot;
        Assert.Equal(2, saved.Version);
        Assert.Equal((new StripSettings(0, 51), new StripSettings(51, 51)), (saved.Boards[0].Left, saved.Boards[0].Right));
        Assert.Equal("001122334455", saved.Boards[0].DeviceMac);
        Assert.Null(saved.Boards[1].Left); Assert.Null(saved.Boards[1].DeviceMac);
        Assert.False(saved.Devices[0].Simulated);
        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var file = await File.ReadAllTextAsync(Path.Combine(path, "settings.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("edges", file); Assert.DoesNotContain("rotation", file); Assert.Contains("\"version\": 2", file);

        // A version 1 backup restored through the API migrates too, and strips that are already there are kept.
        await store.SaveAsync(saved with { Version = 1, Boards = [saved.Boards[0] with { Left = new(51, 51), Right = new(0, 51) }] }, TestContext.Current.CancellationToken);
        Assert.Equal(2, store.Snapshot.Version);
        Assert.Equal(new StripSettings(51, 51), store.Snapshot.Boards[0].Left);
        // A board whose controller is gone cannot be mapped and becomes unassigned instead of failing the load.
        var orphan = InstallationSettingsStore.Migrate(new InstallationSettings { Version = 1, Boards = [new() { Id = "x", Name = "X", DeviceMac = "aabbccddeeff" }] });
        Assert.Null(orphan.Boards[0].DeviceMac);
        InstallationSettingsStore.Validate(orphan);
    }
    [Fact]
    public async Task SimulatedControllersShowTheirFramesWithoutTouchingTheNetwork()
    {
        await using var h = new TestHarness();
        var (store, clock, monitor, output) = (h.Store, h.Clock, h.Monitor, h.Output);
        var first = await h.Registry.AddSimulatedAsync(TestContext.Current.CancellationToken);
        var second = await h.Registry.AddSimulatedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("02b0bf000001", "Simulated board 1", BoardMapping.LedsPerBoard, true, true), (first.Mac, first.Name, first.LedCount, first.Simulated, first.Selected));
        Assert.Equal("02b0bf000002", second.Mac);
        Assert.Equal([first, second], store.Snapshot.Devices);
        var board = BoardMapping.WithDefault(new BoardSettings { Id = "a", Name = "A", DeviceMac = first.Mac }, first.LedCount);
        await store.UpdateAsync(s => s with { Boards = [board],
            IdleSync = new IdleSyncSettings { Enabled = true, MasterMac = first.Mac, Followers = [second.Mac] } }, TestContext.Current.CancellationToken);

        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => output.IsReady);
        Assert.Null(monitor.Current(first.Mac));
        await output.StartCalibrationAsync(new([board], "a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => monitor.Current(first.Mac) is not null);
        // The monitor holds what a real board would show: left strip blue, right strip amber.
        var frame = monitor.Current(first.Mac)!;
        Assert.Equal(BoardMapping.LedsPerBoard * 3, frame.Length);
        Assert.Equal(new byte[] { 0, 96, 160 }, frame[(10 * 3)..(10 * 3 + 3)]);
        Assert.Equal(new byte[] { 160, 96, 0 }, frame[(61 * 3)..(61 * 3 + 3)]);
        await output.StopCalibrationAsync(output.Calibration!.SessionId);
        // Like WLED, a simulated board drops back to idle a second after the last frame.
        clock.Advance(BoardFrameMonitor.Lease + TimeSpan.FromMilliseconds(100));
        Assert.Null(monitor.Current(first.Mac));

        // Locate and a manual effect reach it the same way.
        using var locateCancel = new CancellationTokenSource();
        var locating = output.LocateAsync(second.Mac, locateCancel.Token);
        await Wait.Until(() => monitor.Current(second.Mac) is not null);
        locateCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locating);

        // Nothing went out over UDP or HTTP, discovery does not probe it, and no device error is reported.
        await output.ApplyIdleSyncAsync(TestContext.Current.CancellationToken);
        await h.Discovery.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Empty(h.Transport.Sent); Assert.Empty(h.Http.Requests); Assert.Empty(output.Errors);
        await output.StopAsync(CancellationToken.None);
    }
    [Fact]
    public void PreparationPulsesInFromThePlayerEndsThenChasesAndGo()
    {
        var settings = Installation(); var board = settings.Boards[0];
        static byte Peak(byte[] frame, int pixel) => frame[(pixel * 3)..(pixel * 3 + 3)].Max();
        // Sampled half a second after a count, when the white pop has faded.
        var player = BoardSceneRenderer.Render(settings, board, 16, GamePhase.PreCountdown, 3.5, 5);
        Assert.All(Enumerable.Range(0, 16), i => Assert.True(Peak(player, i) > 100));
        // Both strips show the same picture, mirrored about the middle of the board, and it is not flat.
        foreach (var strip in new[] { 0, 8 })
        {
            for (var p = 0; p < 4; p++)
                Assert.Equal(player[((strip + p) * 3)..((strip + p) * 3 + 3)], player[((strip + 7 - p) * 3)..((strip + 7 - p) * 3 + 3)]);
            Assert.NotEqual(player[(strip * 3)..(strip * 3 + 3)], player[((strip + 3) * 3)..((strip + 3) * 3 + 3)]);
        }
        Assert.Equal(player[..24], player[24..]);
        var all = BoardSceneRenderer.Render(settings, board, 16, GamePhase.PreCountdown, 4.5, 5);
        Assert.All(Enumerable.Range(0, 16), i => Assert.True(Peak(all, i) > 100));
        // GO floods every board in the accent colour with white, never the old pure green.
        var go = BoardSceneRenderer.Render(settings, board, 16, GamePhase.Running, 0, 5);
        Assert.All(Enumerable.Range(0, 16), i =>
        {
            Assert.True(Peak(go, i) >= 200);
            Assert.True(go[i * 3] > 0 && go[i * 3 + 2] > 0);
        });
    }
    [Fact]
    public void ReorderedScannerAndSingleDotUseConfiguredRow()
    {
        var a = Board(); var b = Board("aabbccddeeff", "b");
        var settings = Installation() with { Devices = [Device(), Device("aabbccddeeff")], Boards = [a, b] };
        // Just before a count the scanner is back at the start of the row, on the first board only.
        Assert.Contains(BoardSceneRenderer.Render(settings, a, 16, GamePhase.PreCountdown, 0.9, 5), value => value > 100);
        Assert.All(BoardSceneRenderer.Render(settings, b, 16, GamePhase.PreCountdown, 0.9, 5), value => Assert.Equal(0, value));
        settings = settings with { Boards = [b, a] };
        Assert.Contains(BoardSceneRenderer.Render(settings, b, 16, GamePhase.PreCountdown, 0.9, 5), value => value > 100);
        Assert.All(BoardSceneRenderer.Render(settings, a, 16, GamePhase.PreCountdown, 0.9, 5), value => Assert.Equal(0, value));
        var path = BoardSceneRenderer.DotPath(settings.Boards);
        // After the flares one comet runs the dot path; its head is the brightest pixel of the whole row.
        foreach (var time in new[] { 2d, 2.03, 2.12, 2.9, 4.9 })
        {
            var head = path[(int)(BoardLightShow.Comet(time - settings.Animation.FlashSeconds, settings.Animation).Head * path.Count)];
            var brightest = settings.Boards.SelectMany(board =>
            {
                var frame = BoardSceneRenderer.Render(settings, board, 16, GamePhase.Ending, time, 5);
                return Enumerable.Range(0, 16).Select(pixel => (board.Id, Pixel: pixel, Level: frame[pixel * 3] + frame[pixel * 3 + 1] + frame[pixel * 3 + 2]));
            }).MaxBy(pixel => pixel.Level);
            Assert.Equal(head, (brightest.Id, brightest.Pixel));
        }
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
        var path = TestPaths.NewDataPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), """
            { "version": 1, "controllers": [ { "id": "remote", "name": "Family remote", "address": "192.168.1.30", "port": 80,
              "enabled": true, "showTimer": true, "remoteEnabled": true, "secretName": "busybar_token" } ] }
            """, TestContext.Current.CancellationToken);
        var store = Store(path);
        var bar = Assert.Single(store.Snapshot.Controllers);
        Assert.True(bar.Enabled); Assert.True(bar.RemoteEnabled); Assert.Equal("Family remote", bar.Name);
        await store.SaveAsync(store.Snapshot, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("showTimer", await File.ReadAllTextAsync(Path.Combine(path, "settings.json"), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task LocateOwnsOutputAndRejectsGameManualAndOtherLocate()
    {
        await using var h = new TestHarness();
        var (clock, timer, control) = (h.Clock, h.Countdown, h.Control);
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
        await Wait.Until(() => control.IsReady);
        clock.Advance(CountdownService.PreCountdownDuration);
        await Wait.Until(() => control.Phase == GamePhase.Running);
        Assert.True(timer.IsRunning); Assert.Equal(TimeSpan.FromSeconds(60), timer.Remaining);
        await control.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task DeviceFailureDoesNotPreventOtherFramesAndLocateUsesOnlyTarget()
    {
        await using var h = new TestHarness();
        var store = h.Store;
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }], Boards = [Board(), Board("aabbccddeeff", "b")] }, TestContext.Current.CancellationToken);
        var (control, transport, output) = (h.Control, h.Transport, h.Output);
        using var locateCancel = new CancellationTokenSource();
        var locating = output.LocateAsync("aabbccddeeff", locateCancel.Token);
        Assert.Single(transport.Sent); Assert.Equal("aabbccddeeff", transport.Sent.Single());
        locateCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locating);
        Assert.False(control.IsLocating);
        transport.Sent.Clear(); transport.FailMac = "001122334455";
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(60), deviation: TimeSpan.Zero);
        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => transport.Sent.Contains("aabbccddeeff"));
        Assert.Contains("001122334455", output.Errors.Keys);
        await output.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task SettingsWithoutLightShowLoadDefaultsAndInvalidValuesAreRejected()
    {
        var path = TestPaths.NewDataPath();
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), """{ "version": 1, "animation": { "framesPerSecond": 20 } }""", TestContext.Current.CancellationToken);
        var store = Store(path);
        var saved = store.Snapshot;
        Assert.Equal(new LightShowSettings(), saved.LightShow);
        Assert.Equal("neonSunset", saved.LightShow.Theme); Assert.Equal("calm", saved.LightShow.Intensity);
        Assert.Equal(20, saved.Animation.FramesPerSecond);
        var show = saved.LightShow;
        foreach (var invalid in new[]
        {
            show with { Theme = "disco" }, show with { Intensity = "wild" }, show with { Brightness = 19 }, show with { Brightness = 101 },
            show with { Speed = 24 }, show with { Speed = 201 }, show with { Custom = null! },
            show with { Custom = show.Custom with { Accent = "#000000" } }, show with { Custom = show.Custom with { Primary = "ff4fad" } },
            show with { Custom = show.Custom with { Tertiary = "#ff4fa" } }, show with { Custom = show.Custom with { Secondary = "#gg4fad" } }
        })
            Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { LightShow = invalid }));
        Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(saved with { LightShow = null! }));
        await store.UpdateAsync(s => s with { LightShow = new() { Theme = "custom", Brightness = 60, Speed = 150, Intensity = "lively", Custom = new() { Primary = "#00FF00" } } }, TestContext.Current.CancellationToken);
        var reloaded = Store(path).Snapshot;
        Assert.Equal(("custom", 60, 150, "lively", "#00FF00"), (reloaded.LightShow.Theme, reloaded.LightShow.Brightness, reloaded.LightShow.Speed, reloaded.LightShow.Intensity, reloaded.LightShow.Custom.Primary));
        Assert.Equal(20, reloaded.Animation.FramesPerSecond);
    }
    [Fact]
    public async Task SavedCountdownSurvivesReloadOverridesConfigurationAndIsValidated()
    {
        await using var h = new TestHarness(countdown: new CountdownOptions { DurationSeconds = 600, DeviationSeconds = 120 });
        var (store, countdown) = (h.Store, h.Countdown);
        Assert.Null(store.Snapshot.Countdown);
        Assert.Equal((600, 120), ((int)countdown.CountdownDuration.TotalSeconds, (int)countdown.CountdownDeviation.TotalSeconds)); // Configuration until saved.
        await store.UpdateAsync(s => s with { Countdown = new(720, 30) }, TestContext.Current.CancellationToken);
        Assert.Equal((720, 30), ((int)countdown.CountdownDuration.TotalSeconds, (int)countdown.CountdownDeviation.TotalSeconds));
        Assert.Equal(new CountdownSettings(720, 30), Store(h.DataPath).Snapshot.Countdown); // A restart reads the file again.
        foreach (var invalid in new CountdownSettings[] { new(0, 0), new(86401, 0), new(60, -1), new(60, 60) })
            Assert.Throws<ArgumentException>(() => InstallationSettingsStore.Validate(store.Snapshot with { Countdown = invalid }));
    }
    [Fact]
    public async Task LightTestPlaysUnsavedDraftEndsByItselfAndConflictsCleanly()
    {
        await using var h = new TestHarness();
        var store = h.Store;
        var saved = Installation() with { Devices = [Device(), Device("aabbccddeeff") with { Address = "192.168.1.21" }],
            Boards = [Board(), new(), Board("aabbccddeeff", "b")] };
        await store.SaveAsync(saved, TestContext.Current.CancellationToken);
        var (clock, control, transport, output) = (h.Clock, h.Control, h.Transport, h.Output);
        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => output.IsReady);
        var red = new LightShowSettings { Theme = "custom", Custom = new() { Primary = "#ff0000", Secondary = "#ff0000", Tertiary = "#ff0000", Accent = "#ff0000" } };
        LightShowTestRequest Request(string segment) => new(segment, red, new());

        // A rejected request leaves the boards alone.
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartLightShowTestAsync(Request("idle")));
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartLightShowTestAsync(new("playing", red with { Brightness = 0 }, new())));
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartLightShowTestAsync(new("playing", null!, new())));
        Assert.False(control.IsTestingLights); Assert.Null(output.LightShowTest);

        await output.StartLightShowTestAsync(Request("playing"));
        Assert.True(control.IsTestingLights);
        Assert.Equal("playing", output.LightShowTest!.Segment);
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), output.LightShowTest.EndsUtc);
        Assert.Equal(new LightShowSettings(), store.Snapshot.LightShow); // The draft is played, not saved.
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartGameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.ConfigureAsync(() => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.LocateAsync("001122334455", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartCalibrationAsync(new(saved.Boards, "a")));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => transport.Sent.Contains("001122334455") && transport.Sent.Contains("aabbccddeeff"));
        // Both boards show the draft's colours: red only.
        Assert.All(transport.Frames, frame =>
        {
            Assert.Contains(frame.Pixels, value => value > 0);
            Assert.All(frame.Pixels.Chunk(3), pixel => { Assert.Equal(0, pixel[1]); Assert.Equal(0, pixel[2]); });
        });

        // A second test replaces the first, and a test ends by itself.
        await output.StartLightShowTestAsync(Request("gameOver"));
        Assert.Equal("gameOver", output.LightShowTest!.Segment);
        Assert.True(control.IsTestingLights);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Wait.Until(() => output.LightShowTest is null && !control.IsTestingLights);
        await control.ConfigureAsync(() => Task.CompletedTask);

        // Whoever owns the lights keeps them.
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartLightShowTestAsync(Request("playing")));
        await control.StopManualEffectAsync();
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartLightShowTestAsync(Request("playing")));
        await control.StopGameAsync();
        await output.StartCalibrationAsync(new(saved.Boards, "a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartLightShowTestAsync(Request("playing")));
        await output.StopCalibrationAsync(output.Calibration!.SessionId);
        Assert.False(control.IsTestingLights);

        // Without a board there is nothing to test on.
        await store.UpdateAsync(s => s with { Boards = [new()] }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => output.StartLightShowTestAsync(Request("playing")));
        await store.UpdateAsync(s => s with { Boards = saved.Boards }, TestContext.Current.CancellationToken);

        // Stopping is idempotent, and shutdown releases a running test.
        await output.StopLightShowTestAsync();
        await output.StartLightShowTestAsync(Request("countdown"));
        await output.StopLightShowTestAsync();
        Assert.Null(output.LightShowTest); Assert.False(control.IsTestingLights);
        await output.StartLightShowTestAsync(Request("playing"));
        await output.StopAsync(CancellationToken.None);
        Assert.Null(output.LightShowTest); Assert.False(control.IsTestingLights);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.StartLightShowTestAsync(Request("playing")));
    }
    [Fact]
    public async Task GameWithoutBoardsSendsNoFramesButManualOutputStillDoes()
    {
        await using var h = new TestHarness();
        var store = h.Store;
        await store.SaveAsync(Installation() with { Boards = [new()] }, TestContext.Current.CancellationToken);
        var (clock, control, transport, output) = (h.Clock, h.Control, h.Transport, h.Output);
        await control.StartGameAsync(TimeSpan.FromSeconds(60), TimeSpan.Zero);
        await output.StartAsync(CancellationToken.None);
        await Wait.Until(() => output.IsReady);
        for (var pass = 0; pass < 5; pass++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Empty(transport.Sent);
        // The same loop still mirrors a manual effect to the strip, so it was running all along.
        await control.StopGameAsync();
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => transport.Sent.Contains("001122334455"));
        await output.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task IdleSyncUsesNativeConfigKeysAndMirrorsEffectsWithoutCopyingBoundsOrTurningOnMaster()
    {
        await using var h = new TestHarness();
        var (store, output) = (h.Store, h.Output);
        await store.SaveAsync(Installation() with { Devices = [Device(), Device("aabbccddeeff")], Boards = [],
            IdleSync = new IdleSyncSettings { Enabled = true, MasterMac = "001122334455", Followers = ["aabbccddeeff"], Group = 2 } }, TestContext.Current.CancellationToken);
        // Every controller reports this state when it is read, and accepts whatever it is sent.
        h.Http.Respond = request => FakeHttp.Json(request.Method == HttpMethod.Get
            ? """{"on":false,"bri":100,"mainseg":0,"seg":[{"id":0,"start":0,"stop":16,"fx":7,"col":[[0,0,255]]}]}""" : """{"success":true}""");
        SentRequest[] Posted(string path) => h.Http.Requests.Where(r => r.Method == HttpMethod.Post && r.Path == path).ToArray();
        await output.ApplyIdleSyncAsync(TestContext.Current.CancellationToken);
        var config = Posted("/json/cfg")[0].Json;
        Assert.True(config.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("en").GetBoolean());
        Assert.Equal(2, config.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("grp").GetInt32());
        var states = Posted("/json/state");
        Assert.False(states[0].Json.TryGetProperty("on", out _)); // Never force the master on.
        Assert.False(states[1].Json.GetProperty("on").GetBoolean());
        Assert.Equal(7, states[1].Json.GetProperty("seg").GetProperty("fx").GetInt32());
        Assert.False(states[1].Json.GetProperty("seg").TryGetProperty("start", out _));
        h.Http.Requests.Clear();
        await output.ApplyIdleSyncAsync(TestContext.Current.CancellationToken, configure: false);
        Assert.Empty(Posted("/json/cfg")); // Avoid flash writes every game.
        await store.UpdateAsync(s => s with { IdleSync = s.IdleSync with { Enabled = false } }, TestContext.Current.CancellationToken);
        h.Http.Requests.Clear(); await output.ApplyIdleSyncAsync(TestContext.Current.CancellationToken);
        Assert.All(Posted("/json/cfg"), r => Assert.False(r.Json.GetProperty("if").GetProperty("sync").GetProperty("send").GetProperty("en").GetBoolean()));
    }
}
