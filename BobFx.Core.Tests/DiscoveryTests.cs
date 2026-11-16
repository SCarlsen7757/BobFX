using System.Net;
using Microsoft.Extensions.Configuration;
using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace BobFx.Core.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public async Task RegisteredEndpointsRefreshOnDiskAndManualProbingSurvivesMdnsFailure()
    {
        await using var h = new TestHarness();
        var (store, source, service) = (h.Store, h.DiscoverySource, h.Discovery);
        await store.SaveAsync(new() { Devices = [new("aabbccddeeff", "lamp", "192.168.1.5", 80, 16, 21324)] }, TestContext.Current.CancellationToken);
        source.Advertisements = [new("192.168.1.6", 81, "lamp")];
        h.Http.Respond = request => request.Uri.Host == "192.168.1.5" ? new(HttpStatusCode.NotFound)
            : FakeHttp.Json("""{"brand":"WLED","name":"lamp","ver":"test","mac":"aabbccddeeff","leds":{"count":20},"udpport":21324}""");
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal("192.168.1.6", store.Snapshot.Devices.Single().Address);
        Assert.Equal(20, store.Snapshot.Devices.Single().LedCount);
        source.BeforeResolve = _ => throw new IOException("mDNS unavailable");
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Single(service.Devices, d => d.IsOnline);
        Assert.Equal("192.168.1.6", TestData.Store(h.DataPath).Snapshot.Devices.Single().Address);
    }
    [Fact]
    public async Task SameMacTracksChangedAddressAndPort()
    {
        var source = new FakeDiscovery { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var service = Create(source, _ => Info());
        await service.ScanAsync(TestContext.Current.CancellationToken);
        source.Advertisements = [new("192.168.1.6", 81, "lamp")];
        await service.ScanAsync(TestContext.Current.CancellationToken);
        var device = Assert.Single(service.Devices);
        Assert.Equal(IPAddress.Parse("192.168.1.6"), device.Address);
        Assert.Equal(81, device.Port);
    }

    [Fact]
    public async Task OverlapIsSkippedAndClearInvalidatesPendingScan()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeDiscovery { Advertisements = [new("192.168.1.5", 80, "lamp")], BeforeResolve = async token =>
            { entered.SetResult(); await release.Task.WaitAsync(token); } };
        using var service = Create(source, _ => Info());
        var pending = service.ScanAsync(TestContext.Current.CancellationToken);
        await entered.Task;
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Calls);
        service.Clear();
        release.SetResult();
        await pending;
        Assert.Empty(service.Devices);
    }

    [Theory]
    [InlineData("8.8.8.8", 80)]
    [InlineData("127.0.0.1", 80)]
    [InlineData("192.168.1.5", 0)]
    [InlineData("invalid", 80)]
    public async Task UntrustedAdvertisementsDoNotIssueHttpRequests(string address, int port)
    {
        var source = new FakeDiscovery { Advertisements = [new(address, port, "untrusted")] };
        var requests = 0;
        using var service = Create(source, _ => { requests++; return Info(); });
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, requests);
        Assert.Empty(service.Devices);
    }

    [Fact]
    public async Task OversizedAndRedirectResponsesAreIgnored()
    {
        var source = new FakeDiscovery { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var service = Create(source, _ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70 * 1024)) });
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Empty(service.Devices);
        using var redirect = Create(source, _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://8.8.8.8/") } });
        await redirect.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Empty(redirect.Devices);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesScanGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeDiscovery { BeforeResolve = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        using var service = Create(source, _ => Info());
        using var cancellation = new CancellationTokenSource();
        var pending = service.ScanAsync(cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        source.BeforeResolve = null;
        source.Advertisements = [new("192.168.1.5", 80, "lamp")];
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Single(service.Devices);
    }

    [Fact]
    public async Task MalformedDeviceJsonIsIsolatedButUnexpectedHttpFailuresAreVisible()
    {
        var source = new FakeDiscovery { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var malformed = Create(source, _ => new(HttpStatusCode.OK) { Content = new StringContent("{invalid") });
        await malformed.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Empty(malformed.Devices);
        using var unexpected = Create(source, _ => throw new InvalidOperationException("Unexpected handler failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unexpected.ScanAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(unexpected.LastError);
    }

    [Fact]
    public async Task ResolverFailureIsVisibleAndNextSuccessfulScanClearsDiagnostic()
    {
        var source = new FakeDiscovery { BeforeResolve = _ => throw new InvalidOperationException("test scan failure") };
        using var service = Create(source, _ => Info());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(service.LastError);
        Assert.NotNull(service.LastScanUtc);
        source.BeforeResolve = null;
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task ManualScansAreServerOwnedCoalesceAndCancelOnShutdown()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeDiscovery();
        source.BeforeResolve = async token =>
        {
            if (source.Calls == 1) { first.TrySetResult(); await release.Task.WaitAsync(token); }
            else { second.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        using var service = Create(source, _ => Info());
        await service.StartAsync(CancellationToken.None);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        service.RequestScan(); service.RequestScan();
        Assert.True(service.IsScanning); Assert.Equal(1, source.Calls);
        release.SetResult();
        await Wait.Until(() => !service.IsScanning);
        service.RequestScan();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Calls);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.IsScanning);
    }

    [Fact]
    public async Task OfflineRemovalCannotRemoveAnOnlineDevice()
    {
        var source = new FakeDiscovery { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var service = Create(source, _ => Info());
        await service.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => service.RemoveOffline("aabbccddeeff"));
        Assert.Single(service.Devices);
        Assert.False(service.RemoveOffline("missing"));
    }

    private static HttpResponseMessage Info() => FakeHttp.Json("""{"brand":"WLED","name":"lamp","ver":"test","mac":"aabbccddeeff"}""");
    /// <summary>A discovery service that finds what <paramref name="source"/> advertises and reads each device's
    /// info from <paramref name="response"/>. Nothing is registered, so only discovery itself is exercised.</summary>
    private static WLedDiscoveryService Create(FakeDiscovery source, Func<SentRequest, HttpResponseMessage> response) =>
        new(NullLogger<WLedDiscoveryService>.Instance, Options.Create(new WLedOptions()), new FakeHttp(response), source, TestData.Store(), TimeProvider.System);
}
