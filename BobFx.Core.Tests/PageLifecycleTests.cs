using System.Reflection;
using BobFx.Core.Components.Pages;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class PageLifecycleTests
{
    [Fact]
    public async Task HomeDoesNotRenderAfterDisposalWhileStopCommandIsPending()
    {
        var clock = new FakeTimeProvider();
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), clock);
        using var control = new RgbAndSoundControlService(countdown, rgb,
            new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance);
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        var page = new Home();
        Set(page, "CountdownService", countdown);
        Set(page, "Control", control);
        Set(page, "Clock", clock);
        Set(page, "Logger", NullLogger<Home>.Instance);
        Set(page, "JSRuntime", new Confirmed());
        Read(page);
        var state = Field<CountdownSnapshot>(page, "state");
        var commands = Field<SemaphoreSlim>(control, "commands");
        await commands.WaitAsync();
        Task pending;
        try
        {
            pending = (Task)Call(page, "StopCountdown")!;
            Assert.False(pending.IsCompleted);
            await page.DisposeAsync();
        }
        finally { commands.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(state, Field<CountdownSnapshot>(page, "state"));
        Assert.Null(Field<long?>(page, "suppressedEndRunId"));
    }

    [Fact]
    public async Task DialogCloseDoesNotMutatePageAfterDisposalWhileInteropIsPending()
    {
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance,
            Options.Create(new WLedOptions()), new NoHttp(), new PendingDiscovery());
        var module = new PendingClose();
        var page = new WLed();
        Set(page, "Discovery", discovery);
        Set(page, "Logger", NullLogger<WLed>.Instance);
        Set(page, "ShowModal", true);
        SetField(page, "module", module);
        var close = (Task)Call(page, "QueueDialogAsync", false)!;
        Assert.False(close.IsCompleted);
        var dispose = page.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        module.Closed.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        await close;
        Assert.True((bool)typeof(WLed).GetProperty("ShowModal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
        Assert.True(module.Disposed);
    }

    [Fact]
    public async Task NavigationCancelsScanAndWaitsForItsCompletionWithoutUpdatingPage()
    {
        var source = new PendingDiscovery();
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance,
            Options.Create(new WLedOptions()), new NoHttp(), source);
        var page = new WLed();
        Set(page, "Discovery", discovery);
        Set(page, "Logger", NullLogger<WLed>.Instance);
        Call(page, "OnInitialized");
        Assert.Single(Subscribers(discovery));
        var scan = (Task)Call(page, "Refresh")!;
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(((Task)Call(page, "Refresh")!).IsCompleted); // no second scan while one is active
        var dispose = page.DisposeAsync().AsTask();
        await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(dispose.IsCompleted);
        Assert.Empty(Subscribers(discovery));
        source.Release.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        await scan;
        Assert.Null(Field<string?>(page, "scanStatus"));
        Assert.True(Field<bool>(page, "scanning")); // no late finally mutation after disposal
        Assert.Empty(discovery.Devices);
    }

    [Fact]
    public async Task RepeatedNavigationReturnsDiscoverySubscriptionsToBaseline()
    {
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance,
            Options.Create(new WLedOptions()), new NoHttp(), new PendingDiscovery());
        for (var visit = 0; visit < 5; visit++)
        {
            var page = new WLed();
            Set(page, "Discovery", discovery);
            Set(page, "Logger", NullLogger<WLed>.Instance);
            Call(page, "OnInitialized");
            Assert.Single(Subscribers(discovery));
            await page.DisposeAsync();
            Assert.Empty(Subscribers(discovery));
            var before = Field<int>(page, "refreshRequested");
            discovery.Clear();
            Assert.Equal(before, Field<int>(page, "refreshRequested"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalWaitsForLateDialogModuleAndReleasesIt(bool disconnected)
    {
        var imported = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new DialogModule(disconnected);
        using var discovery = new WLedDiscoveryService(NullLogger<WLedDiscoveryService>.Instance,
            Options.Create(new WLedOptions()), new NoHttp(), new PendingDiscovery());
        var page = new WLed();
        Set(page, "Discovery", discovery);
        Set(page, "Logger", NullLogger<WLed>.Instance);
        Set(page, "JS", new PendingImport(imported.Task));
        var opening = (Task)Call(page, "QueueDialogAsync", true)!;
        var dispose = page.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        imported.SetResult(module);
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        await opening;
        Assert.Equal(1, module.Disposals);
        Assert.Equal(0, module.Opens);
    }

    [Fact]
    public async Task HomeUsesCurrentRunSnapshotAcrossRapidTransitionsAndNavigation()
    {
        var clock = new FakeTimeProvider();
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        var page = new Home();
        Set(page, "CountdownService", countdown);
        Set(page, "Clock", clock);
        countdown.Start(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        Read(page);
        Assert.True(Field<bool>(page, "showBobMessage"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Read(page);
        Assert.False(Field<bool>(page, "showBobMessage"));
        countdown.Stop();
        Read(page);
        Assert.True(Field<bool>(page, "showGameOver"));
        SetField(page, "suppressedEndRunId", countdown.Snapshot.RunId);
        Read(page);
        Assert.False(Field<bool>(page, "showGameOver"));
        countdown.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Read(page);
        Assert.False(Field<bool>(page, "showGameOver"));
        Assert.False(Field<bool>(page, "showBobMessage"));
        countdown.Stop();
        Assert.Null(countdown.Snapshot.EndedUtc);
        clock.Advance(TimeSpan.FromSeconds(20));
        Read(page);
        Assert.False(Field<bool>(page, "showGameOver"));
        countdown.Start(false, duration: TimeSpan.FromSeconds(1), deviation: TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(1));
        countdown.Advance();
        // Completion followed by a restart before the next UI tick must not show the old ending.
        countdown.Start(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        Read(page);
        Assert.True(Field<bool>(page, "showBobMessage"));
        Assert.False(Field<bool>(page, "showGameOver"));
        await page.DisposeAsync();
        var nextPage = new Home();
        Set(nextPage, "CountdownService", countdown);
        Set(nextPage, "Clock", clock);
        Read(nextPage);
        Assert.True(Field<bool>(nextPage, "showBobMessage"));
        await nextPage.DisposeAsync();
    }

    [Fact]
    public void SnapshotTimestampsPreserveSchedulerOvershootAndResetForNewRuns()
    {
        var clock = new FakeTimeProvider();
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        var initial = clock.GetUtcNow();
        countdown.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(2));
        countdown.Advance();
        clock.Advance(TimeSpan.FromSeconds(5));
        countdown.Advance();
        Assert.Equal(initial.AddSeconds(5), countdown.Snapshot.StartedUtc);
        clock.Advance(TimeSpan.FromSeconds(9));
        countdown.Advance();
        Assert.Equal(initial.AddSeconds(15), countdown.Snapshot.EndedUtc);
        countdown.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Assert.Null(countdown.Snapshot.StartedUtc);
        Assert.Null(countdown.Snapshot.EndedUtc);
    }

    private static Delegate[] Subscribers(WLedDiscoveryService service) =>
        Field<Delegate?>(service, "DevicesUpdated")?.GetInvocationList() ?? [];
    private static void Read(Home page) => Call(page, "ReadState");
    private static object? Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Set(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP request expected");
    }
    private sealed class PendingDiscovery : IWLedDiscoverySource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.SetResult();
            await Release.Task;
            token.ThrowIfCancellationRequested();
            return [];
        }
    }
    private sealed class PendingImport(Task<IJSObjectReference> imported) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => (TValue)await imported;
    }
    private sealed class Confirmed : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult((TValue)(object)true);
    }
    private sealed class PendingClose : IJSObjectReference
    {
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("close", identifier);
            // Simulate an already dispatched interop operation completing after cancellation.
            await Closed.Task;
            return default!;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class DialogModule(bool disconnected) : IJSObjectReference
    {
        public int Disposals { get; private set; }
        public int Opens { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        { Opens++; return ValueTask.FromResult(default(TValue)!); }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return disconnected ? ValueTask.FromException(new JSDisconnectedException("Test disconnect")) : ValueTask.CompletedTask;
        }
    }
}
