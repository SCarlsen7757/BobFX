using System.Net;
using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace BobFx.Core.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public async Task SameMacTracksChangedAddressAndPort()
    {
        var source = new Source { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var service = Create(source, _ => Info());
        await service.ScanAsync();
        source.Advertisements = [new("192.168.1.6", 81, "lamp")];
        await service.ScanAsync();
        var device = Assert.Single(service.Devices);
        Assert.Equal(IPAddress.Parse("192.168.1.6"), device.Address);
        Assert.Equal(81, device.Port);
    }

    [Fact]
    public async Task OverlapIsSkippedAndClearInvalidatesPendingScan()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Advertisements = [new("192.168.1.5", 80, "lamp")], BeforeResolve = async token =>
            { entered.SetResult(); await release.Task.WaitAsync(token); } };
        using var service = Create(source, _ => Info());
        var pending = service.ScanAsync();
        await entered.Task;
        await service.ScanAsync();
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
        var source = new Source { Advertisements = [new(address, port, "untrusted")] };
        var requests = 0;
        using var service = Create(source, _ => { requests++; return Info(); });
        await service.ScanAsync();
        Assert.Equal(0, requests);
        Assert.Empty(service.Devices);
    }

    [Fact]
    public async Task OversizedAndRedirectResponsesAreIgnored()
    {
        var source = new Source { Advertisements = [new("192.168.1.5", 80, "lamp")] };
        using var service = Create(source, _ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70 * 1024)) });
        await service.ScanAsync();
        Assert.Empty(service.Devices);
        using var redirect = Create(source, _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://8.8.8.8/") } });
        await redirect.ScanAsync();
        Assert.Empty(redirect.Devices);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesScanGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { BeforeResolve = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        using var service = Create(source, _ => Info());
        using var cancellation = new CancellationTokenSource();
        var pending = service.ScanAsync(cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        source.BeforeResolve = null;
        source.Advertisements = [new("192.168.1.5", 80, "lamp")];
        await service.ScanAsync();
        Assert.Single(service.Devices);
    }

    [Fact]
    public async Task ResolverFailureIsVisibleAndNextSuccessfulScanClearsDiagnostic()
    {
        var source = new Source { BeforeResolve = _ => throw new InvalidOperationException("test scan failure") };
        using var service = Create(source, _ => Info());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync());
        Assert.NotNull(service.LastError);
        Assert.NotNull(service.LastScanUtc);
        source.BeforeResolve = null;
        await service.ScanAsync();
        Assert.Null(service.LastError);
    }

    private static HttpResponseMessage Info() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"brand":"WLED","name":"lamp","ver":"test","mac":"aabbccddeeff"}""")
    };
    private static WLedDiscoveryService Create(Source source, Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(NullLogger<WLedDiscoveryService>.Instance, Options.Create(new WLedOptions()), new Factory(new Handler(response)), source);
    private sealed class Source : IWLedDiscoverySource
    {
        public IReadOnlyList<WLedAdvertisement> Advertisements { get; set; } = [];
        public Func<CancellationToken, Task>? BeforeResolve { get; set; }
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (BeforeResolve is not null) await BeforeResolve(cancellationToken);
            return Advertisements;
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(2) };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
