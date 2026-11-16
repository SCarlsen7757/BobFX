using BobFx.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
namespace BobFx.Core.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("AllowedHosts", "*", "Production requires explicit AllowedHosts")]
    [InlineData("AllowedHosts", "", "Production requires explicit AllowedHosts")]
    [InlineData("Security:AllowedClientNetworks:0", "not-a-cidr", "AllowedClientNetworks")]
    [InlineData("WLed:Led:LedCount", "491", "1–490 LEDs")]
    [InlineData("WLed:Discovery:UpdateIntervalSeconds", "4", "discovery interval")]
    [InlineData("Countdown:DeviationSeconds", "720", "deviation smaller than duration")]
    public void InvalidProductionConfigurationFailsBeforeServing(string key, string value, string expected)
    {
        using var app = new BobFxFactory(new Dictionary<string, string?> { [key] = value }, background: false);
        var failure = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(expected, failure.ToString(), StringComparison.Ordinal);
    }
}
