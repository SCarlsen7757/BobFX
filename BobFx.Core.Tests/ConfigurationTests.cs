using BobFx.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
namespace BobFx.Core.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("AllowedHosts", "*", "Production requires explicit AllowedHosts")]
    [InlineData("AllowedHosts", "", "Production requires explicit AllowedHosts")]
    [InlineData("Security:AllowedClientNetworks:0", "not-a-cidr", "AllowedClientNetworks")]
    [InlineData("WLed:Led:LedCount", "491", "1–490 LEDs")]
    [InlineData("WLed:Udp:TargetPort", "0", "valid port")]
    [InlineData("Countdown:DeviationSeconds", "600", "deviation smaller than duration")]
    public void InvalidProductionConfigurationFailsBeforeServing(string key, string value, string expected)
    {
        using var app = new InvalidApp(key, value);
        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(expected, failure.ToString(), StringComparison.Ordinal);
    }
    private sealed class InvalidApp(string key, string value) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "localhost",
                    ["Security:AllowedClientNetworks:0"] = "127.0.0.0/8",
                    ["WLed:Udp:TargetAddress"] = "127.0.0.1"
                };
                values[key] = value;
                config.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
