using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class BusyBarModeTests
{
    private static byte[] Selector(int position) => [0x12, 6, 0x5a, 4, 0x12, 2, 8, (byte)position];
    [Theory]
    [InlineData(0, BusyBarSelector.Busy)]
    [InlineData(1, BusyBarSelector.Custom)]
    [InlineData(2, BusyBarSelector.Off)]
    [InlineData(3, BusyBarSelector.Apps)]
    [InlineData(4, BusyBarSelector.Settings)]
    [InlineData(99, BusyBarSelector.Unknown)]
    public void DecodeSelectorPositionsAndPreserveEventOrder(int position, BusyBarSelector expected)
    {
        var events = BusyBarInputDecoder.Events(Selector(position).Concat(new byte[] { 0x12, 6, 0x5a, 4, 0x0a, 2, 8, 2 }).ToArray());
        Assert.Equal(2, events.Count); Assert.Equal(expected, events[0].Selector); Assert.Equal(2, events[1].Button);
        Assert.Equal(BusyBarSelector.Busy, Assert.Single(BusyBarInputDecoder.Events([0x12, 4, 0x5a, 2, 0x12, 0])).Selector);
        Assert.Empty(BusyBarInputDecoder.Presses(Selector(position)));
        Assert.Throws<InvalidDataException>(() => BusyBarInputDecoder.Events(Selector(3).Concat(new byte[] { 0x12, 99 }).ToArray()));
    }
    [Fact]
    public async Task GateAllowsOnlyAppsAndRevokesWhenSelectorBecomesUnknown()
    {
        var gate = new BusyBarModeGate(); var sent = 0;
        Task Send() { sent++; return Task.CompletedTask; }
        Assert.False(await gate.InAppsAsync(Send, CancellationToken.None));
        await gate.SetAsync(BusyBarSelector.Apps);
        Assert.True(await gate.InAppsAsync(Send, CancellationToken.None));
        foreach (var position in new[] { BusyBarSelector.Busy, BusyBarSelector.Custom, BusyBarSelector.Off, BusyBarSelector.Settings, BusyBarSelector.Unknown })
        {
            await gate.SetAsync(position);
            Assert.False(await gate.InAppsAsync(Send, CancellationToken.None));
        }
        Assert.Equal(1, sent);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceMonitorsSelectorWithoutRemoteControlsAndPausesOnLeaveOrStreamLoss(bool remoteEnabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Settings:DataPath"] = Path.Combine(Path.GetTempPath(), "bobfx-mode-" + Guid.NewGuid().ToString("N")) }).Build();
        var settings = new InstallationSettingsStore(config);
        var credentials = new ControllerCredentials(settings);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30", Enabled = true, RemoteEnabled = remoteEnabled };
        await settings.SaveAsync(new() { Controllers = [bar] });
        await credentials.SetAsync(bar.Id, Guid.NewGuid().ToString("N"));
        var source = new StatusSource(); var clients = new Clients();
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), TimeProvider.System);
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), TimeProvider.System);
        using var control = new RgbAndSoundControlService(countdown, rgb, new SoundService(NullLogger<SoundService>.Instance), TimeProvider.System, NullLogger<RgbAndSoundControlService>.Instance);
        using var service = new BusyBarService(settings, control, countdown, clients, config, TimeProvider.System, credentials, source);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => source.Connected);
            await Task.Delay(150);
            Assert.Empty(clients.DisplayRequests);
            // Buttons outside APPS must leave the native device's controls alone.
            source.Messages.Writer.TryWrite([0x12, 6, 0x5a, 4, 0x0a, 2, 8, 2]);
            source.Messages.Writer.TryWrite(Selector(3));
            await Eventually(() => clients.DisplayRequests.Contains(HttpMethod.Post));
            Assert.Equal(GamePhase.Idle, control.Phase);
            Assert.Equal(BusyBarSelector.Apps, service.Statuses[bar.Id].Selector);
            source.Messages.Writer.TryWrite(Selector(1));
            await Eventually(() => service.Statuses[bar.Id].Selector == BusyBarSelector.Custom);
            var paused = clients.DisplayRequests.Count;
            await Task.Delay(200);
            Assert.Equal(paused, clients.DisplayRequests.Count);
            source.Messages.Writer.TryWrite(Selector(3));
            await Eventually(() => clients.DisplayRequests.Count > paused);
            if (remoteEnabled)
            {
                source.Messages.Writer.TryWrite([0x12, 6, 0x5a, 4, 0x0a, 2, 8, 2]);
                await Eventually(() => control.Phase == GamePhase.PreCountdown);
            }
            source.Messages.Writer.Complete();
            await Eventually(() => service.Statuses[bar.Id].Selector == BusyBarSelector.Unknown);
            var disconnected = clients.DisplayRequests.Count;
            await Task.Delay(200);
            Assert.Equal(disconnected, clients.DisplayRequests.Count);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
    private sealed class StatusSource : IBusyBarStatusSource
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
    private sealed class Clients : IHttpClientFactory
    {
        public ConcurrentQueue<HttpMethod> DisplayRequests { get; } = new();
        public HttpClient CreateClient(string name) => new(new Handler(DisplayRequests));
    }
    private sealed class Handler(ConcurrentQueue<HttpMethod> displayRequests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var version = request.RequestUri!.AbsolutePath == "/api/version";
            if (!version) displayRequests.Enqueue(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(version ? """{"api_semver":"27.5.0"}""" : "{}", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
