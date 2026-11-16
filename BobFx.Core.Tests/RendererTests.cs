using System.Numerics;
using BobFx.Core.Services;
using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
namespace BobFx.Core.Tests;

public sealed class RendererTests
{
    [Fact]
    public async Task RendererFaultStopsStreamingAndValidCommandRecoversReadiness()
    {
        await using var h = new TestHarness();
        var (clock, rgb) = (h.Clock, h.Rgb);
        await rgb.StartAsync(CancellationToken.None);
        await Wait.Until(() => rgb.IsReady);
        await rgb.StartEffectAsync(new FaultingEffect());
        rgb.IsStreaming = true;
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Wait.Until(() => rgb.LastError is not null && !rgb.IsStreaming);
        Assert.False(rgb.IsStreaming);
        Assert.All(rgb.GetSnapshot(), color => Assert.Equal(Vector3.Zero, color));
        await rgb.StartEffectAsync(new SolidEffect { Color = Vector3.One });
        Assert.Null(rgb.LastError);
        await rgb.StopAsync(CancellationToken.None);
        Assert.False(rgb.IsReady);
    }

    [Fact]
    public async Task SpeedChangeKeepsTheEffectOnTheInjectedClock()
    {
        await using var h = new TestHarness(wled: new WLedOptions { Led = new() { LedCount = 1 } });
        var (clock, rgb) = (h.Clock, h.Rgb);
        await rgb.StartAsync(CancellationToken.None);
        await Wait.Until(() => rgb.IsReady);
        await rgb.StartEffectAsync(new FadeInEffect { TargetColor = Vector3.One, FadeDuration = TimeSpan.FromSeconds(2) });
        rgb.SetSpeed(TimeSpan.FromMilliseconds(10)); // Restarts the fade; it must keep following the renderer's clock.
        clock.Advance(TimeSpan.FromSeconds(2));
        await Wait.Until(() => rgb.GetSnapshot()[0] == Vector3.One);
        await rgb.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NaturalGameEndEmitsOneSoundAndReturnsToIdleAfterTheEnding()
    {
        await using var h = new TestHarness();
        var (clock, rgb, control) = (h.Clock, h.Rgb, h.Control);
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        h.Sounds.SoundRequested += requests.Enqueue;
        await control.StartAsync(CancellationToken.None);
        await Wait.Until(() => control.IsReady);
        await control.StartGameAsync(false, duration: TimeSpan.FromSeconds(10), deviation: TimeSpan.Zero);
        Assert.Empty(requests);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Wait.Until(() => control.Phase == GamePhase.Ending && requests.Count == 1);
        Assert.Single(requests);
        // The ending lasts as long as the flares and the comet together.
        var animation = h.Store.Snapshot.Animation;
        clock.Advance(TimeSpan.FromSeconds(animation.FlashSeconds + animation.DotSeconds));
        await Wait.Until(() => control.Phase == GamePhase.Idle);
        Assert.False(rgb.IsStreaming);
        Assert.Single(requests);
        await control.StopAsync(CancellationToken.None);
    }
    private sealed class FaultingEffect : RgbEffectBase
    {
        private bool initialized;
        public FaultingEffect() => Speed = TimeSpan.FromMilliseconds(10);
        public override RgbEffect EffectType => RgbEffect.Solid;
        public override bool Apply(Span<Vector3> leds)
        {
            if (initialized) throw new InvalidOperationException("test renderer failure");
            initialized = true; leds.Fill(Vector3.One); return false;
        }
        public override RgbEffectInfo GetInfo() => new() { DefaultSpeed = TimeSpan.FromMilliseconds(10) };
    }
}
