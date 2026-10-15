using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class ControllerCredentialsTests
{
    [Fact]
    public async Task PasswordsPersistProtectedAndAreSeparateFromExportedSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "bobfx-credentials-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:DataPath"] = path }).Build();
        var settings = new InstallationSettingsStore(config);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30" };
        await settings.SaveAsync(new() { Controllers = [bar] });
        var credentials = new ControllerCredentials(settings);
        var password = Guid.NewGuid().ToString("N");
        Assert.False(credentials.HasPassword(bar.Id));
        await credentials.SetAsync(bar.Id, password);
        Assert.True(credentials.HasPassword(bar.Id));
        Assert.Equal(1, credentials.Revision(bar.Id));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(Path.Combine(path, "settings.json")));
        var protectedFile = Assert.Single(Directory.GetFiles(Path.Combine(path, "credentials"), "*.protected"));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(protectedFile));
        var reopened = new ControllerCredentials(new InstallationSettingsStore(config));
        Assert.Equal(password, reopened.Read(bar.Id));
        Assert.Throws<FileNotFoundException>(() => reopened.Read("other-controller"));
        await credentials.SetAsync(bar.Id, password + "-replacement");
        Assert.Equal(2, credentials.Revision(bar.Id));
        Assert.Equal(password + "-replacement", credentials.Read(bar.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => credentials.SetAsync(bar.Id, "bad\r\nheader"));
        Assert.Equal(password + "-replacement", credentials.Read(bar.Id));
    }
    [Fact]
    public async Task LocalHttpConnectionTestUsesSavedPasswordWithoutEnablingDevice()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Settings:DataPath"] = Path.Combine(Path.GetTempPath(), "bobfx-credentials-" + Guid.NewGuid().ToString("N")) }).Build();
        var settings = new InstallationSettingsStore(config);
        var credentials = new ControllerCredentials(settings);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30" };
        var password = Guid.NewGuid().ToString("N");
        await credentials.SetAsync(bar.Id, password);
        var clients = new TestClients(password);
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), TimeProvider.System);
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), TimeProvider.System);
        using var control = new RgbAndSoundControlService(countdown, rgb, new SoundService(NullLogger<SoundService>.Instance), TimeProvider.System, NullLogger<RgbAndSoundControlService>.Instance);
        using var service = new BusyBarService(settings, control, countdown, clients, config, TimeProvider.System, credentials);
        Assert.Equal("27.5.0", await service.TestConnectionAsync(bar));
        Assert.Equal(1, clients.Calls);
        Assert.Equal(GamePhase.Idle, control.Phase);
        Assert.False(bar.Enabled);
    }
    private sealed class TestClients(string password) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(request =>
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http", request.RequestUri!.Scheme);
            Assert.Equal("192.168.1.30", request.RequestUri.Host);
            Assert.Equal(80, request.RequestUri.Port);
            Assert.Equal("/api/version", request.RequestUri.AbsolutePath);
            Assert.Equal(password, request.Headers.GetValues("X-API-Token").Single());
        }));
    }
    [Fact]
    public async Task EnabledControllerReconnectsWhenItsSavedPasswordChanges()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Settings:DataPath"] = Path.Combine(Path.GetTempPath(), "bobfx-credentials-" + Guid.NewGuid().ToString("N")) }).Build();
        var settings = new InstallationSettingsStore(config);
        var credentials = new ControllerCredentials(settings);
        var bar = new BusyBarSettings { Id = "remote", Address = "192.168.1.30", Enabled = true };
        await settings.SaveAsync(new() { Controllers = [bar] });
        var first = Guid.NewGuid().ToString("N"); var replacement = Guid.NewGuid().ToString("N");
        await credentials.SetAsync(bar.Id, first);
        var clients = new ReconnectClients();
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), TimeProvider.System);
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), TimeProvider.System);
        using var control = new RgbAndSoundControlService(countdown, rgb, new SoundService(NullLogger<SoundService>.Instance), TimeProvider.System, NullLogger<RgbAndSoundControlService>.Instance);
        using var service = new BusyBarService(settings, control, countdown, clients, config, TimeProvider.System, credentials, new WaitingStatusSource());
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!clients.Passwords.Contains(first)) await Task.Delay(10, deadline.Token);
            await credentials.SetAsync(bar.Id, replacement);
            while (!clients.Passwords.Contains(replacement)) await Task.Delay(10, deadline.Token);
            Assert.Equal(GamePhase.Idle, control.Phase);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }
    private sealed class ReconnectClients : IHttpClientFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Passwords { get; } = new();
        public HttpClient CreateClient(string name) => new(new ReconnectHandler(Passwords));
    }
    private sealed class WaitingStatusSource : IBusyBarStatusSource
    {
        public async IAsyncEnumerable<byte[]> ReadAsync(BusyBarSettings bar, string password, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }
    private sealed class ReconnectHandler(System.Collections.Concurrent.ConcurrentQueue<string> passwords) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var version = request.RequestUri!.AbsolutePath == "/api/version";
            if (version) passwords.Enqueue(request.Headers.GetValues("X-API-Token").Single());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(version ? """{"api_semver":"27.5.0"}""" : "{}", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Handler(Action<HttpRequestMessage> check) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            check(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"api_semver":"27.5.0"}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
