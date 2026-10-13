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
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/countdown/start-with-pre", new { preDurationSeconds = 30, durationSeconds = 60, deviationSeconds = 0 })).StatusCode);
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
        foreach (var path in new[] { "/", "/countdown", "/drgb", "/wled", "/app.css", "/js/sounds.js", "/js/dialog.js", "/sounds/game_over/game-over-yeah.mp3" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("lib/bootstrap/", html);
        Assert.Contains("Enable audio", html);
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
                ["WLed:Udp:TargetAddress"] = "127.0.0.1",
            }));
            builder.ConfigureTestServices(services =>
            {
                // Do not open discovery or UDP sockets in HTTP contract tests.
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<DRgbService>());
                services.AddHostedService(sp => sp.GetRequiredService<RgbAndSoundControlService>());
                services.AddSingleton<IStartupFilter, TestClientAddress>();
            });
        }
    }

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
