using System.Net;
using System.Numerics;
using System.Text.Json;
using BobFx.Core.Controllers;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class BackendTests
{
    private static CountdownService Countdown(FakeTimeProvider clock) => new(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
    private static DRgbService Renderer(FakeTimeProvider clock, int count = 30) => new(NullLogger<DRgbService>.Instance,
        Options.Create(new WLedOptions { Led = new() { LedCount = count } }), new RgbEffectFactory(), clock);

    [Fact]
    public void RestartCannotBeEndedByOldCountdown()
    {
        var clock = new FakeTimeProvider();
        var timer = Countdown(clock);
        var endings = 0;
        timer.OnEndOfEvent += () => endings++;
        timer.Start(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        var firstId = timer.Snapshot.RunId;
        timer.Stop();
        timer.Start(false, duration: TimeSpan.FromSeconds(20), deviation: TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(11));
        timer.Advance();
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(9), timer.Remaining);
        Assert.Equal(firstId + 1, timer.Snapshot.RunId);
        Assert.Equal(1, endings);
    }

    [Fact]
    public void CountdownUsesElapsedTimeAndCanceledPreDoesNotEmitEnd()
    {
        var clock = new FakeTimeProvider();
        var timer = Countdown(clock);
        var endings = 0;
        timer.OnEndOfEvent += () => endings++;
        timer.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(2));
        timer.Advance();
        Assert.Equal(TimeSpan.FromSeconds(3), timer.PreCountdownRemaining);
        timer.Stop();
        Assert.Equal(0, endings);
        timer.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(7));
        timer.Advance();
        Assert.Equal(TimeSpan.FromSeconds(8), timer.Remaining);
        clock.Advance(TimeSpan.FromSeconds(8));
        timer.Advance();
        timer.Advance();
        Assert.False(timer.IsRunning);
        Assert.Equal(1, endings);
    }

    [Fact]
    public async Task ManualCommandsActivateStreamingAndCannotOverrideGame()
    {
        var clock = new FakeTimeProvider();
        var timer = Countdown(clock);
        using var rgb = Renderer(clock);
        using var control = new RgbAndSoundControlService(timer, rgb,
            new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance);
        await control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Solid).WithColor("#ff0000"));
        Assert.True(rgb.IsUdpActive);
        await control.StartGameAsync(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartManualEffectAsync(b => b.WithEffect(RgbEffect.Rainbow)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StopManualEffectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.SetManualSpeedAsync(TimeSpan.FromMilliseconds(100)));
        await control.StopGameAsync();
        Assert.Equal(GamePhase.Idle, control.Phase);
        Assert.False(rgb.IsUdpActive);
    }

    [Fact]
    public async Task NewGameCancelsEndingAndShutdownCompletes()
    {
        var clock = new FakeTimeProvider();
        using var rgb = Renderer(clock);
        var timer = Countdown(clock);
        using var control = new RgbAndSoundControlService(timer, rgb,
            new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance);
        await control.StartAsync(CancellationToken.None);
        await Eventually(() => control.IsReady);
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        await control.StopGameAsync();
        Assert.Equal(GamePhase.Ending, control.Phase);
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(20), deviation: TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Eventually(() => timer.Remaining <= TimeSpan.FromSeconds(14));
        Assert.Equal(GamePhase.Running, control.Phase);
        Assert.True(rgb.IsUdpActive);
        await control.StopAsync(CancellationToken.None);
        Assert.False(control.IsReady);
        Assert.False(rgb.IsUdpActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartGameAsync());
    }

    [Fact]
    public async Task ParallelCommandsCannotLeaveMultipleGameRuns()
    {
        var clock = new FakeTimeProvider();
        using var rgb = Renderer(clock);
        var timer = Countdown(clock);
        using var control = new RgbAndSoundControlService(timer, rgb,
            new SoundService(NullLogger<SoundService>.Instance), clock, NullLogger<RgbAndSoundControlService>.Instance);
        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            try { await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero); return true; }
            catch (InvalidOperationException) { return false; }
        });
        Assert.Equal(1, (await Task.WhenAll(attempts)).Count(success => success));
        Assert.Equal(1, timer.Snapshot.RunId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(490)]
    public async Task DrgbFrameHasProtocolHeaderAndExactBoundaryPayload(int count)
    {
        var clock = new FakeTimeProvider();
        using var rgb = Renderer(clock, count);
        await rgb.StartEffectAsync(new SolidEffect { Color = new Vector3(1, 0.5f, 0) });
        var snapshot = rgb.GetSnapshot();
        snapshot[0] = Vector3.Zero;
        var buffer = new byte[count * 3 + 2];
        Assert.Equal(buffer.Length, WLedBroadcastService.WriteFrame(rgb, buffer));
        Assert.Equal((byte)2, buffer[0]);
        Assert.Equal((byte)1, buffer[1]);
        Assert.Equal(new byte[] { 255, 127, 0 }, buffer[^3..]);
        Assert.Equal(new byte[] { 255, 127, 0 }, buffer[2..5]);
    }

    [Fact]
    public async Task InvalidUpdatePreservesCurrentEffect()
    {
        var clock = new FakeTimeProvider();
        using var rgb = Renderer(clock);
        await rgb.StartEffectAsync(new BreathingEffect());
        Assert.Throws<ArgumentException>(() => rgb.UpdateEffect(e => ((BreathingEffect)e).MaxBrightness = float.NaN));
        Assert.Equal(RgbEffect.Breathing, rgb.CurrentEffect);
        Assert.Throws<ArgumentException>(() => rgb.SetSpeed(TimeSpan.Zero));
    }

    [Fact]
    public void FadeUsesInjectedMonotonicClock()
    {
        var clock = new FakeTimeProvider();
        var effect = new FadeInEffect { TargetColor = Vector3.One, FadeDuration = TimeSpan.FromSeconds(2) };
        effect.Initialize(1, clock);
        var leds = new Vector3[1];
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(effect.Apply(leds, CancellationToken.None));
        Assert.Equal(new Vector3(0.5f), leds[0]);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(effect.Apply(leds, CancellationToken.None));
    }

    [Fact]
    public void MetadataSerializesAndFactoryUsesItsDefaultSpeed()
    {
        var factory = new RgbEffectFactory();
        foreach (var type in factory.GetRegisteredEffects())
        {
            var effect = factory.Create(type);
            var json = JsonSerializer.Serialize(effect.GetInfo());
            Assert.NotEmpty(json);
            Assert.Equal(effect.GetInfo().DefaultSpeed, effect.Speed);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(490, true)]
    [InlineData(491, false)]
    public void OptionsEnforceProtocolBoundaries(int count, bool valid) =>
        Assert.Equal(valid, new WLedOptions { Led = new() { LedCount = count } }.IsValid());

    [Theory]
    [InlineData("http://bobfx.local", "bobfx.local:80", true)]
    [InlineData("http://bobfx.local:8080", "bobfx.local:8080", true)]
    [InlineData("http://evil.local", "bobfx.local", false)]
    [InlineData("null", "bobfx.local", false)]
    [InlineData("http://bobfx.local/path", "bobfx.local", false)]
    public void OriginPolicyComparesCanonicalOrigin(string origin, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http"; context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;
        Assert.Equal(expected, SecurityOptions.IsSameOrigin(context.Request));
    }

    [Fact]
    public void NetworkAndDiscoveryBoundariesAreEnforced()
    {
        var security = new SecurityOptions { AllowedClientNetworks = ["192.168.1.0/24"] };
        Assert.True(security.Allows(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.False(security.Allows(IPAddress.Parse("192.168.2.5")));
        Assert.False(WLedDiscoveryService.IsLocalDeviceAddress(IPAddress.Parse("127.0.0.1")));
        Assert.False(WLedDiscoveryService.IsLocalDeviceAddress(IPAddress.Parse("8.8.8.8")));
        Assert.True(WLedDiscoveryService.IsLocalDeviceAddress(IPAddress.Parse("192.168.1.5")));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

}
