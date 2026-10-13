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
}
