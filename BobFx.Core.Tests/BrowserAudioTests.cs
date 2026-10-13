using System.Reflection;
using BobFx.Core.Components.Layout;
using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class BrowserAudioTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task DisposalReleasesManagedCallbackEvenWhenCircuitDisconnects(bool sessionDisconnected, bool moduleDisconnected, bool initializationDisconnected)
    {
        var component = Create();
        var callback = DotNetObjectReference.Create(component);
        var module = new Module(sessionDisconnected, moduleDisconnected);
        SetField(component, "callback", callback);
        SetField(component, "module", module);
        if (initializationDisconnected)
            SetField(component, "initialization", Task.FromException(new JSDisconnectedException("Test disconnected initialization")));

        await component.DisposeAsync();

        Assert.Equal(1, module.Disposals);
        Assert.Equal(initializationDisconnected ? 0 : 1, module.SessionCleanups);
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
    }

    [Fact]
    public async Task DisposalDuringImportWaitsAndReleasesLateModule()
    {
        var component = Create();
        var import = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetProperty(component, "JS", new Runtime(import.Task));
        var initialize = (Task)typeof(BrowserAudio).GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;
        SetField(component, "initialization", initialize);
        var dispose = component.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        var module = new Module(false, false);
        import.SetResult(module);
        await dispose;
        Assert.Equal(1, module.Disposals);
        Assert.Null(typeof(BrowserAudio).GetField("callback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component));
    }

    [Fact]
    public async Task DisposalDuringSessionCreationCleansUpRealSessionAndCallback()
    {
        var component = Create();
        var module = new PendingSession();
        SetProperty(component, "JS", new Runtime(Task.FromResult<IJSObjectReference>(module)));
        var initialize = (Task)typeof(BrowserAudio).GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;
        SetField(component, "initialization", initialize);
        var callback = (DotNetObjectReference<BrowserAudio>)typeof(BrowserAudio).GetField("callback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        var dispose = component.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        module.Session.SetResult(42);
        await dispose;
        Assert.Equal(42, module.DisposedSession);
        Assert.True(module.Disposed);
        Assert.Throws<ObjectDisposedException>(() => callback.Value);
    }

    [Fact]
    public async Task UnexpectedDisposalFailureIsNotHidden()
    {
        var component = Create();
        SetField(component, "module", new Module(false, false, unexpectedFailure: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => component.DisposeAsync().AsTask());
    }

    private static BrowserAudio Create()
    {
        var component = new BrowserAudio();
        SetProperty(component, "Sounds", new SoundService(NullLogger<SoundService>.Instance));
        SetProperty(component, "Logger", NullLogger<BrowserAudio>.Instance);
        return component;
    }

    // Set the interop references that Blazor normally supplies after first render.
    // HtmlRenderer doesn't run OnAfterRender/JS interop, and no browser is needed for this failure path.
    private static void SetField(BrowserAudio component, string name, object value) =>
        typeof(BrowserAudio).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
    private static void SetProperty(BrowserAudio component, string name, object value) =>
        typeof(BrowserAudio).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);

    private sealed class Runtime(Task<IJSObjectReference> import) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            return (TValue)await import;
        }
    }

    private sealed class Module(bool sessionDisconnected, bool moduleDisconnected, bool unexpectedFailure = false) : IJSObjectReference
    {
        public int SessionCleanups { get; private set; }
        public int Disposals { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("dispose", identifier);
            SessionCleanups++;
            if (unexpectedFailure) throw new InvalidOperationException("Unexpected test failure");
            if (sessionDisconnected) throw new JSDisconnectedException("Test disconnected session cleanup");
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return moduleDisconnected ? ValueTask.FromException(new JSDisconnectedException("Test disconnected module disposal")) : ValueTask.CompletedTask;
        }
    }

    private sealed class PendingSession : IJSObjectReference
    {
        public TaskCompletionSource<int> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposedSession { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "initialize") return (TValue)(object)await Session.Task;
            Assert.Equal("dispose", identifier);
            DisposedSession = (int)args![0]!;
            return default!;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
