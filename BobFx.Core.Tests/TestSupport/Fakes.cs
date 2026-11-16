using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BobFx.Core.Services;

namespace BobFx.Core.Tests;

/// <summary>What a service sent over HTTP, kept after the request itself is gone.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, string? ApiToken, string? ContentType, byte[] Body)
{
    public string Path => Uri.AbsolutePath;
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>Stands in for every device a service talks to over HTTP. It records each request and answers with
/// <c>{}</c> unless the test says otherwise.</summary>
internal sealed class FakeHttp(Func<SentRequest, HttpResponseMessage>? respond = null) : IHttpClientFactory
{
    public ConcurrentQueue<SentRequest> Requests { get; } = new();
    /// <summary>Replaces the answers, for a test whose device changes its mind.</summary>
    public Func<SentRequest, HttpResponseMessage>? Respond { get; set; } = respond;

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpClient CreateClient(string name) => new(new Handler(this)) { Timeout = TimeSpan.FromSeconds(2) };

    private sealed class Handler(FakeHttp owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var sent = new SentRequest(request.Method, request.RequestUri!,
                request.Headers.TryGetValues("X-API-Token", out var tokens) ? tokens.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(token));
            owner.Requests.Enqueue(sent);
            return owner.Respond?.Invoke(sent) ?? Json("{}");
        }
    }
}

/// <summary>Stands in for mDNS: the test says which devices advertise themselves.</summary>
internal sealed class FakeDiscovery : IWLedDiscoverySource
{
    public IReadOnlyList<WLedAdvertisement> Advertisements { get; set; } = [];
    /// <summary>Runs inside the resolve, to hold a scan open or make it fail.</summary>
    public Func<CancellationToken, Task>? BeforeResolve { get; set; }
    public int Calls { get; private set; }

    public async Task<IReadOnlyList<WLedAdvertisement>> ResolveAsync(CancellationToken cancellationToken)
    {
        Calls++;
        if (BeforeResolve is not null) await BeforeResolve(cancellationToken);
        return Advertisements;
    }
}

/// <summary>Stands in for UDP: it keeps every frame instead of sending it.</summary>
internal sealed class RecordingTransport : IWLedFrameTransport
{
    public ConcurrentQueue<string> Sent { get; } = new();
    public ConcurrentQueue<(string Mac, byte[] Pixels)> Frames { get; } = new();
    /// <summary>A controller that cannot be reached.</summary>
    public string? FailMac { get; set; }

    public Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        if (device.Mac == FailMac) throw new IOException("Test device offline.");
        Sent.Enqueue(device.Mac); Frames.Enqueue((device.Mac, pixels.ToArray()));
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for a BUSY Bar's status stream. The test writes the messages the bar would send; a stream
/// that is never written to stays open like an idle bar's.</summary>
internal sealed class FakeStatusSource : IBusyBarStatusSource
{
    private int connected;
    public bool Connected => Volatile.Read(ref connected) != 0;
    public Channel<byte[]> Messages { get; } = Channel.CreateUnbounded<byte[]>();

    public async IAsyncEnumerable<byte[]> ReadAsync(BusyBarSettings bar, string password, [EnumeratorCancellation] CancellationToken token)
    {
        Volatile.Write(ref connected, 1);
        await foreach (var message in Messages.Reader.ReadAllAsync(token)) yield return message;
    }
}
