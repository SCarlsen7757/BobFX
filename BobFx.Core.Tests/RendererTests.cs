using System.Numerics;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
namespace BobFx.Core.Tests;

public sealed class RendererTests
{
    [Fact]
    public async Task RendererFaultStopsStreamingAndValidCommandRecoversReadiness()
    {
        var clock = new FakeTimeProvider();
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), clock);
        await rgb.StartAsync(CancellationToken.None);
        await Eventually(() => rgb.IsReady);
        await rgb.StartEffectAsync(new FaultingEffect());
        rgb.IsUdpActive = true;
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Eventually(() => rgb.LastError is not null && !rgb.IsUdpActive);
        Assert.False(rgb.IsUdpActive);
        Assert.All(rgb.GetSnapshot(), color => Assert.Equal(Vector3.Zero, color));
        await rgb.StartEffectAsync(new SolidEffect { Color = Vector3.One });
        Assert.Null(rgb.LastError);
        await rgb.StopAsync(CancellationToken.None);
        Assert.False(rgb.IsReady);
    }

    [Fact]
    public async Task NaturalGameEndEmitsOneSoundAndReturnsToIdleAfterFiveSeconds()
    {
        var clock = new FakeTimeProvider();
        using var rgb = new DRgbService(NullLogger<DRgbService>.Instance, Options.Create(new WLedOptions()), new RgbEffectFactory(), clock);
        var timer = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        var sounds = new SoundService(NullLogger<SoundService>.Instance);
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sounds.SoundRequested += requests.Enqueue;
        using var control = new RgbAndSoundControlService(timer, rgb, sounds, clock, NullLogger<RgbAndSoundControlService>.Instance);
        await control.StartAsync(CancellationToken.None);
        await Eventually(() => control.IsReady);
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        Assert.Empty(requests);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => control.Phase == GamePhase.Ending && requests.Count == 1);
        Assert.Single(requests);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Eventually(() => control.Phase == GamePhase.Idle);
        Assert.False(rgb.IsUdpActive);
        Assert.Single(requests);
        await control.StopAsync(CancellationToken.None);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private sealed class FaultingEffect : RgbEffectBase
    {
        private bool initialized;
        public FaultingEffect() => Speed = TimeSpan.FromMilliseconds(10);
        public override RgbEffect EffectType => RgbEffect.Solid;
        public override bool Apply(Span<Vector3> leds, CancellationToken token)
        {
            if (initialized) throw new InvalidOperationException("test renderer failure");
            initialized = true; leds.Fill(Vector3.One); return false;
        }
        public override RgbEffectInfo GetInfo() => new() { DefaultSpeed = TimeSpan.FromMilliseconds(10) };
        public override IRgbEffect Clone() => new FaultingEffect();
    }
}
