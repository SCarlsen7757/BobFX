using BobFx.Core.Services;
using System.Net;
using System.Text;

namespace BobFx.Core.Tests;

public sealed class BusyBarModeTests
{
    [Theory]
    [InlineData(0, BusyBarSelector.Busy)]
    [InlineData(1, BusyBarSelector.Custom)]
    [InlineData(2, BusyBarSelector.Off)]
    [InlineData(3, BusyBarSelector.Apps)]
    [InlineData(4, BusyBarSelector.Settings)]
    [InlineData(99, BusyBarSelector.Unknown)]
    public void DecodeSelectorPositionsAndPreserveEventOrder(int position, BusyBarSelector expected)
    {
        var events = BusyBarInputDecoder.Events([.. BusyBarFrames.Selector(position), .. BusyBarFrames.StartPressed]);
        Assert.Equal(2, events.Count); Assert.Equal(expected, events[0].Selector); Assert.Equal(2, events[1].Button);
        Assert.Empty(BusyBarInputDecoder.Presses(BusyBarFrames.Selector(position)));
    }
    [Fact]
    public void SelectorWithoutAPositionIsBusyAndATruncatedMessageIsRejected()
    {
        // Position 0 is the protobuf default and is left out of the message.
        Assert.Equal(BusyBarSelector.Busy, Assert.Single(BusyBarInputDecoder.Events([0x12, 4, 0x5a, 2, 0x12, 0])).Selector);
        Assert.Throws<InvalidDataException>(() => BusyBarInputDecoder.Events([.. BusyBarFrames.Selector(3), 0x12, 99]));
    }
    [Fact]
    public async Task GateAllowsOnlyAppsAndRevokesWhenSelectorBecomesUnknown()
    {
        var gate = new BusyBarModeGate(); var sent = 0;
        Task Send() { sent++; return Task.CompletedTask; }
        Assert.False(await gate.InAppsAsync(Send, CancellationToken.None));
        await gate.SetAsync(BusyBarSelector.Apps, TestContext.Current.CancellationToken);
        Assert.True(await gate.InAppsAsync(Send, CancellationToken.None));
        foreach (var position in new[] { BusyBarSelector.Busy, BusyBarSelector.Custom, BusyBarSelector.Off, BusyBarSelector.Settings, BusyBarSelector.Unknown })
        {
            await gate.SetAsync(position, TestContext.Current.CancellationToken);
            Assert.False(await gate.InAppsAsync(Send, CancellationToken.None));
        }
        Assert.Equal(1, sent);
    }

    // These run on real time: the service paces itself with the clock, and the tests check what it does not do
    // while that time passes.
    [Theory]
    [InlineData(false, false, "27.14.0")]
    [InlineData(true, false, "27.14.0")]
    [InlineData(false, true, "27.14.0")]
    [InlineData(true, false, "27.5.0")]
    public async Task ServiceMonitorsSelectorWithoutRemoteControlsAndPausesOnLeaveOrStreamLoss(bool remoteEnabled, bool failedHeartbeat, string api)
    {
        await using var bar = await Bar.StartAsync(api, remoteEnabled);
        var (control, service, source) = (bar.Harness.Control, bar.Harness.BusyBars, bar.Harness.BusyBarStatus);
        await Wait.Until(() => source.Connected);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Empty(bar.Display());
        Assert.Empty(bar.Uploads());
        // Buttons outside APPS must leave the native device's controls alone.
        source.Messages.Writer.TryWrite(BusyBarFrames.StartPressed);
        source.Messages.Writer.TryWrite(BusyBarFrames.Selector(3));
        await Wait.Until(() => bar.Draws().Length > 0);
        Assert.Equal(5, bar.Uploads().Length);
        var posts = bar.Draws().Length;
        await Task.Delay(350, TestContext.Current.CancellationToken);
        Assert.InRange(bar.Draws().Length - posts, 0, 1);
        Assert.Equal(5, bar.Uploads().Length); // Animation frames never trigger uploads.
        if (failedHeartbeat)
        {
            Interlocked.Exchange(ref bar.FailNextDraw, 1);
            var beforeFailure = bar.Display().Length;
            await Wait.Until(() => bar.Display().Length >= beforeFailure + 2);
            Assert.Equal(5, bar.Uploads().Length); // A failed heartbeat must not re-upload assets.
        }
        Assert.Equal(GamePhase.Idle, control.Phase);
        Assert.Equal(BusyBarSelector.Apps, service.Statuses[Bar.Id].Selector);
        source.Messages.Writer.TryWrite(BusyBarFrames.Selector(1));
        await Wait.Until(() => service.Statuses[Bar.Id].Selector == BusyBarSelector.Custom);
        var paused = bar.Display().Length;
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(paused, bar.Display().Length);
        Assert.Equal(5, bar.Uploads().Length);
        source.Messages.Writer.TryWrite(BusyBarFrames.Selector(3));
        await Wait.Until(() => bar.Display().Length > paused);
        Assert.Equal(5, bar.Uploads().Length); // Re-enter APPS using the preloaded files.
        if (remoteEnabled)
        {
            var beforeStart = bar.Display().Length;
            source.Messages.Writer.TryWrite(BusyBarFrames.StartPressed);
            await Wait.Until(() => control.Phase == GamePhase.PreCountdown);
            await Wait.Until(() => bar.Display().Length > beforeStart);
            Assert.Contains("-countdown3-", bar.DrawnPaths().Last());
            // The bar counts down by itself: no command per second while the clip plays.
            var duringCountdown = bar.Display().Length;
            await Task.Delay(1300, TestContext.Current.CancellationToken);
            Assert.Equal(duringCountdown, bar.Display().Length);
            // BACK is ignored; pressing the wheel (OK, the protobuf default button) stops the game.
            source.Messages.Writer.TryWrite(BusyBarFrames.BackPressed);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(GamePhase.PreCountdown, control.Phase);
            source.Messages.Writer.TryWrite(BusyBarFrames.WheelPressed);
            await Wait.Until(() => control.Phase == GamePhase.Idle);
            await Wait.Until(() => bar.Display().Length > duringCountdown);
            Assert.Contains("-idle-", bar.DrawnPaths().Last()); // The countdown is replaced in one draw.
        }
        source.Messages.Writer.Complete();
        await Wait.Until(() => service.Statuses[Bar.Id].Selector == BusyBarSelector.Unknown);
        var disconnected = bar.Display().Length;
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(disconnected, bar.Display().Length);
        Assert.DoesNotContain(bar.Display(), request => request.Method == HttpMethod.Delete); // No gap exposing the clock.
        bar.AssertEveryRequestIsWellFormed();
    }
    [Theory]
    [InlineData("27.9.0")]
    [InlineData("28.0.0")]
    public async Task UnreviewedApiVersionsGetNoUploadsDrawsOrInputStream(string api)
    {
        await using var bar = await Bar.StartAsync(api, remoteEnabled: true);
        var service = bar.Harness.BusyBars;
        await Wait.Until(() => service.Statuses.TryGetValue(Bar.Id, out var status) && status.ApiVersion == api);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var paused = service.Statuses[Bar.Id];
        Assert.Contains("output paused", paused.State);
        Assert.False(paused.InputSupported);
        Assert.False(bar.Harness.BusyBarStatus.Connected);
        Assert.Empty(bar.Uploads());
        Assert.Empty(bar.Display());
    }

    /// <summary>One enabled BUSY Bar with a saved password, answering with the given API version, and the service
    /// that drives it already started.</summary>
    private sealed class Bar(TestHarness harness, string api) : IAsyncDisposable
    {
        public const string Id = "remote";
        public TestHarness Harness => harness;
        /// <summary>Set to 1 to have the bar refuse the next draw once.</summary>
        public int FailNextDraw;

        public static async Task<Bar> StartAsync(string api, bool remoteEnabled)
        {
            var harness = new TestHarness(TimeProvider.System);
            var bar = new Bar(harness, api);
            await harness.Store.SaveAsync(new() { Controllers = [new BusyBarSettings { Id = Id, Address = "192.168.1.30", Enabled = true, RemoteEnabled = remoteEnabled }] },
                TestContext.Current.CancellationToken);
            await harness.Credentials.SetAsync(Id, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
            harness.Http.Respond = request => request.Path == "/api/version" ? FakeHttp.Json($$"""{"api_semver":"{{api}}"}""")
                : IsDraw(request) && Interlocked.Exchange(ref bar.FailNextDraw, 0) == 1 ? new(HttpStatusCode.ServiceUnavailable)
                : FakeHttp.Json("{}");
            await harness.BusyBars.StartAsync(CancellationToken.None);
            return bar;
        }
        private static bool IsDraw(SentRequest request) => request.Method == HttpMethod.Post && request.Path == "/api/display/draw";

        public SentRequest[] Uploads() => [.. harness.Http.Requests.Where(request => request.Path == "/api/assets/upload")];
        /// <summary>Everything sent to the display, whatever the method.</summary>
        public SentRequest[] Display() => [.. harness.Http.Requests.Where(request => request.Path is not ("/api/version" or "/api/assets/upload"))];
        public SentRequest[] Draws() => [.. Display().Where(request => request.Method == HttpMethod.Post)];
        public string[] DrawnPaths() => [.. Draws().Select(request => request.Json.GetProperty("elements")[0].GetProperty("path").GetString()!)];

        /// <summary>Uploads are animation files in the format this API plays; a draw shows exactly one uploaded
        /// animation, and a looping one renews its lease.</summary>
        public void AssertEveryRequestIsWellFormed()
        {
            Assert.All(Uploads(), upload =>
            {
                Assert.Equal("application/octet-stream", upload.ContentType);
                Assert.Equal(api == "27.5.0" ? "bicycle0" : "bicycle1", Encoding.ASCII.GetString(upload.Body[..8]));
            });
            Assert.All(Draws(), draw =>
            {
                var element = Assert.Single(draw.Json.GetProperty("elements").EnumerateArray());
                Assert.Equal("animation", element.GetProperty("type").GetString());
                Assert.Contains(Uploads(), upload => upload.Uri.Query.EndsWith(element.GetProperty("path").GetString()!, StringComparison.Ordinal));
                if (element.GetProperty("loop").GetBoolean()) Assert.Equal(BusyBarService.DisplayLeaseSeconds, element.GetProperty("timeout").GetInt32());
            });
        }
        public ValueTask DisposeAsync() => harness.DisposeAsync();
    }
}
