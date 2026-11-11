using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BobFx.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task ControllerPasswordCanBeWrittenOverLocalHttpButNeverReadBack()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var settings = new InstallationSettings { Controllers = [new BusyBarSettings { Id = "remote", Address = "192.168.1.30" }] };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/installation", settings)).StatusCode);
        var password = Guid.NewGuid().ToString("N");
        var endpoint = "/api/installation/controllers/remote/password";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(endpoint, new { password })).StatusCode);
        Assert.Equal("{\"hasPassword\":true}", await client.GetStringAsync(endpoint));
        Assert.DoesNotContain(password, await client.GetStringAsync("/api/installation"));
        Assert.DoesNotContain(password, await client.GetStringAsync("/controllers"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(endpoint, new { password = "bad\r\nheader" })).StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = JsonContent.Create(new { password = "replacement" }) };
        foreign.Headers.Add("Origin", "http://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/countdown/start", new { durationSeconds = 60, deviationSeconds = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(endpoint, new { password = "replacement" })).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedAndNullSpeedsUseEveryEffectDefault(bool explicitNull)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var effects = await client.GetFromJsonAsync<JsonElement>("/api/drgb/effects");
        foreach (var descriptor in effects.EnumerateArray())
        {
            var name = descriptor.GetProperty("type").GetString();
            var body = new Dictionary<string, object?> { ["effect"] = name };
            if (explicitNull) body["speedMs"] = null;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/drgb/start", body)).StatusCode);
            var status = await client.GetFromJsonAsync<JsonElement>("/api/drgb/status");
            Assert.Equal(name, status.GetProperty("currentEffect").GetString());
            Assert.Equal(TimeSpan.Parse(descriptor.GetProperty("info").GetProperty("defaultSpeed").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture).TotalMilliseconds, status.GetProperty("speed").GetDouble());
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60000)]
    public async Task ExplicitSpeedOverridesDefaultAndInvalidSpeedPreservesOutput(int speed)
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/drgb/start", new { effect = "Strobe", speedMs = speed })).StatusCode);
        foreach (var invalid in new[] { -1, 0, 9, 60001 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/drgb/start", new { effect = "Rainbow", speedMs = invalid })).StatusCode);
            var status = await client.GetFromJsonAsync<JsonElement>("/api/drgb/status");
            Assert.Equal("Strobe", status.GetProperty("currentEffect").GetString());
            Assert.Equal(speed, status.GetProperty("speed").GetDouble());
        }
    }

    [Fact]
    public async Task ProductionRejectsForeignClientsHostsAndOrigins()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        // Hosted workers start asynchronously; allow startup without masking a failure.
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await client.GetAsync("/health/ready", startupTimeout.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(10, startupTimeout.Token);
        var diagnostics = await client.GetFromJsonAsync<JsonElement>("/health/status");
        Assert.True(diagnostics.GetProperty("renderer").GetProperty("ready").GetBoolean());
        Assert.Equal(0, diagnostics.GetProperty("discovery").GetProperty("devices").GetInt32());

        using var outsider = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        outsider.Headers.Add("X-Test-Client-IP", "192.168.2.50");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(outsider)).StatusCode);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/countdown/start") { Content = JsonContent.Create(new { }) };
        foreignOrigin.Headers.Add("Origin", "http://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreignOrigin)).StatusCode);

        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        foreignHost.Headers.Host = "untrusted.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(foreignHost)).StatusCode);
    }

    [Fact]
    public async Task JsonCommandsHaveAtomicValidationAndGameOwnership()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/drgb/start", new { effect = "Solid", speedMs = 100 })).StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/drgb/status")).GetProperty("streaming").GetBoolean());

        var invalid = await client.PostAsJsonAsync("/api/drgb/start", new { effect = "999", speedMs = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Solid", (await client.GetFromJsonAsync<JsonElement>("/api/drgb/status")).GetProperty("currentEffect").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/countdown/start", new { durationSeconds = 5, deviationSeconds = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/countdown/start", new { durationSeconds = 60, deviationSeconds = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/drgb/speed", new { speedMs = 100 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/countdown/stop", null)).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/countdown/status");
        Assert.Equal("Idle", status.GetProperty("phase").GetString());
        Assert.False(status.GetProperty("running").GetBoolean());
        Assert.False(status.GetProperty("preCountdownRunning").GetBoolean());
    }

    [Fact]
    public async Task EffectsAndPublishedOperatorPagesAndAssetsAreAvailable()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var effects = await client.GetFromJsonAsync<JsonElement>("/api/drgb/effects");
        Assert.True(effects.GetArrayLength() >= 10);
        foreach (var path in new[] { "/", "/game/fullscreen", "/countdown", "/lights", "/drgb", "/wled", "/ui/index.html", "/api/ui/state", "/api/wled/devices", "/sounds/game_over/game-over-yeah.mp3" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("blazor", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/ui/assets/", html);
        Assert.Contains("id=\"app\"", html);
        foreach (var path in new[] { "/api/missing", "/hubs/missing", "/health/missing", "/ui/assets/missing.js", "/_framework/blazor.web.js" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AdvancedEffectParametersValidateAtomicallyAndReachRenderer()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/drgb/start",
            new { effect = "Solid", colors = new[] { "#123456" } })).StatusCode);
        var pixel = app.Services.GetRequiredService<DRgbService>().GetSnapshot()[0];
        Assert.Equal(0x12 / 255f, pixel.X); Assert.Equal(0x34 / 255f, pixel.Y); Assert.Equal(0x56 / 255f, pixel.Z);
        foreach (var invalid in new object[] {
            new { effect = "Solid", colors = new[] { "#000000" } },
            new { effect = "Strobe", colors = Array.Empty<string>() },
            new { effect = "Solid", colors = new[] { "#xxxxxx" } },
            new { effect = "FadeIn", fadeDurationSeconds = 0 },
            new { effect = "Breathing", breathDurationSeconds = 61 },
            new { effect = "Breathing", minBrightnessPercent = 80, maxBrightnessPercent = 20 },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/drgb/start", invalid)).StatusCode);
            Assert.Equal("Solid", (await client.GetFromJsonAsync<JsonElement>("/api/drgb/status")).GetProperty("currentEffect").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/drgb/start", new {
            effect = "Breathing", colors = new[] { "#ff00ff" }, breathDurationSeconds = 4, minBrightnessPercent = 15, maxBrightnessPercent = 75
        })).StatusCode);
    }

    [Fact]
    public async Task UiSnapshotsHaveStableVersionsAndReflectGameTransitions()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var first = (await client.GetFromJsonAsync<JsonElement>("/api/ui/state")).GetProperty("game");
        var same = (await client.GetFromJsonAsync<JsonElement>("/api/ui/state")).GetProperty("game");
        Assert.Equal(first.GetProperty("revision").GetInt64(), same.GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/countdown/start", new { })).StatusCode);
        var changed = (await client.GetFromJsonAsync<JsonElement>("/api/ui/state")).GetProperty("game");
        Assert.Equal(first.GetProperty("serverInstanceId").GetString(), changed.GetProperty("serverInstanceId").GetString());
        Assert.True(changed.GetProperty("revision").GetInt64() > first.GetProperty("revision").GetInt64());
        Assert.Equal("PreCountdown", changed.GetProperty("value").GetProperty("phase").GetString());
    }

    private static InstallationSettings OneBoard() => new()
    {
        Devices = [new("001122334455", "Board", "192.168.1.20", 80, 16, 21324)],
        Boards = [new() { Id = "a", Name = "A", DeviceMac = "001122334455", Edges = [new(0, 4), new(4, 4), new(8, 4), new(12, 4)] }]
    };

    [Fact]
    public async Task LightShowSectionSavePreservesOtherSections()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/installation", OneBoard())).StatusCode);
        var themes = await client.GetFromJsonAsync<JsonElement>("/api/installation/light-show/themes");
        Assert.Equal(new[] { "neonSunset", "aurora", "ember" }, themes.EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal("#ff4fad", themes[0].GetProperty("primary").GetString());

        var animation = new { framesPerSecond = 25, flashSeconds = 2, dotSeconds = 4, dotLapsPerSecond = 3 };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/installation/light-show",
            new { lightShow = new { theme = "aurora", brightness = 60, speed = 150, intensity = "lively" }, animation })).StatusCode);
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/installation");
        Assert.Equal("aurora", saved.GetProperty("lightShow").GetProperty("theme").GetString());
        Assert.Equal(60, saved.GetProperty("lightShow").GetProperty("brightness").GetInt32());
        Assert.Equal("#ff4fad", saved.GetProperty("lightShow").GetProperty("custom").GetProperty("primary").GetString());
        Assert.Equal(4, saved.GetProperty("animation").GetProperty("dotSeconds").GetDouble());
        Assert.Equal(1, saved.GetProperty("boards").GetArrayLength());

        // Board layout no longer owns the animation timing, even when an old page still sends it.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/installation/layout",
            new { boards = OneBoard().Boards, idleSync = new IdleSyncSettings(), animation = new { framesPerSecond = 59 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/installation/light-show",
            new { lightShow = new { theme = "aurora", brightness = 5 }, animation })).StatusCode);
        saved = await client.GetFromJsonAsync<JsonElement>("/api/installation");
        Assert.Equal(25, saved.GetProperty("animation").GetProperty("framesPerSecond").GetInt32());
        Assert.Equal(60, saved.GetProperty("lightShow").GetProperty("brightness").GetInt32());
    }

    [Fact]
    public async Task LightShowTestOwnsTheLightsUntilStoppedAndConflictsReturn409()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
        var test = new { segment = "playing", lightShow = new { theme = "ember" }, animation = new { } };
        // Nothing to test on before a board is assigned.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/installation/light-show/test", test)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/installation", OneBoard())).StatusCode);

        var started = await client.PostAsJsonAsync("/api/installation/light-show/test", test);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("playing", (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("segment").GetString());
        var state = await client.GetFromJsonAsync<JsonElement>("/api/ui/state");
        Assert.Equal("playing", state.GetProperty("installation").GetProperty("value").GetProperty("lightShowTest").GetProperty("segment").GetString());
        Assert.Equal("neonSunset", state.GetProperty("installation").GetProperty("value").GetProperty("settings").GetProperty("lightShow").GetProperty("theme").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/countdown/start", new { durationSeconds = 60, deviationSeconds = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/drgb/start", new { effect = "Solid" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/installation/light-show", new { lightShow = new { }, animation = new { } })).StatusCode);
        // Another segment replaces the running test; an unknown one is rejected and changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/installation/light-show/test", new { segment = "gameOver", lightShow = new { }, animation = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/installation/light-show/test", new { segment = "idle", lightShow = new { }, animation = new { } })).StatusCode);
        state = await client.GetFromJsonAsync<JsonElement>("/api/ui/state");
        Assert.Equal("gameOver", state.GetProperty("installation").GetProperty("value").GetProperty("lightShowTest").GetProperty("segment").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/installation/light-show/test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/installation/light-show/test")).StatusCode);
        state = await client.GetFromJsonAsync<JsonElement>("/api/ui/state");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("installation").GetProperty("value").GetProperty("lightShowTest").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/countdown/start", new { durationSeconds = 60, deviationSeconds = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/installation/light-show/test", test)).StatusCode);
    }

    private sealed class TestApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost",
                ["Security:AllowedClientNetworks:0"] = "127.0.0.0/8",
                ["Settings:DataPath"] = Path.Combine(Path.GetTempPath(), "bobfx-http-" + Guid.NewGuid().ToString("N")),
            }));
            builder.ConfigureTestServices(services =>
            {
                // Do not open discovery or UDP sockets in HTTP contract tests.
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<DRgbService>());
                services.AddHostedService(sp => sp.GetRequiredService<RgbAndSoundControlService>());
                services.AddSingleton<IWLedFrameTransport, NoDeviceTransport>();
                services.AddHostedService(sp => sp.GetRequiredService<InstallationOutputService>());
                services.AddSingleton<IStartupFilter, TestClientAddress>();
            });
        }
    }

    private sealed class NoDeviceTransport : IWLedFrameTransport
    { public Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token) => Task.CompletedTask; }

    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Client-IP"].FirstOrDefault() ?? "127.0.0.1");
                return continuation(context);
            });
            next(app);
        };
    }
}
