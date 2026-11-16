using System.Net;
using BobFx.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BobFx.Core.Tests;

/// <summary>
/// The whole application as Production runs it, with the smallest configuration Production accepts and a data
/// folder of its own. Its background services run as they do for real; only what would reach the network is
/// replaced: mDNS, device HTTP, UDP frames and the BUSY Bar status stream.
/// </summary>
/// <param name="configuration">Settings that replace or add to the minimal ones.</param>
/// <param name="background">False for a test that only needs startup, such as one that expects it to fail.</param>
internal sealed class BobFxFactory(IReadOnlyDictionary<string, string?>? configuration = null, bool background = true) : WebApplicationFactory<Program>
{
    public FakeHttp DeviceHttp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost",
                ["Security:AllowedClientNetworks:0"] = "127.0.0.0/8",
                ["Settings:DataPath"] = TestPaths.NewDataPath(),
            };
            foreach (var (key, value) in configuration ?? new Dictionary<string, string?>()) values[key] = value;
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            if (!background) { services.RemoveAll<IHostedService>(); return; }
            services.Replace(ServiceDescriptor.Singleton<IWLedDiscoverySource>(new FakeDiscovery()));
            services.Replace(ServiceDescriptor.Singleton<IWLedFrameTransport>(new RecordingTransport()));
            services.Replace(ServiceDescriptor.Singleton<IBusyBarStatusSource>(new FakeStatusSource()));
            services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(DeviceHttp));
            services.AddSingleton<IStartupFilter, TestClientAddress>();
        });
    }

    /// <summary>The test server has no connection, so the client address the network policy checks comes from a
    /// header: loopback unless a test says otherwise.</summary>
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
