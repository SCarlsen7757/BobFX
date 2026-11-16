using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Options;
using System.Numerics;
namespace BobFx.Core.Services;

/// <summary>Draws the effect an operator started by hand on a virtual strip of LEDs, which the output service
/// stretches over every selected controller. It owns that strip; everyone else gets copies. During a game it
/// draws nothing: the boards are then lit from the scene clock.</summary>
public sealed class ManualEffectRenderer : BackgroundService
{
    private readonly Lock sync = new();
    private readonly Vector3[] leds;
    private readonly IRgbEffectFactory factory;
    private readonly ILogger<ManualEffectRenderer> logger;
    private readonly TimeProvider clock;
    private IRgbEffect? currentEffect;
    private bool completed;
    private long lastFrame;
    private int streaming;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    private string? lastError;
    public string? LastError => Volatile.Read(ref lastError);
    /// <summary>True while frames are to go out to the controllers, for a game as well as for a manual effect.
    /// The coordinator switches it; the output service acts on it.</summary>
    public bool IsStreaming { get => Volatile.Read(ref streaming) != 0; internal set => Volatile.Write(ref streaming, value ? 1 : 0); }
    public RgbEffect CurrentEffect { get { lock (sync) return currentEffect?.EffectType ?? RgbEffect.Off; } }
    public ManualEffectRenderer(ILogger<ManualEffectRenderer> logger, IOptions<WLedOptions> options, IRgbEffectFactory factory, TimeProvider clock)
    {
        this.logger = logger; this.factory = factory; this.clock = clock;
        leds = new Vector3[options.Value.Led.LedCount];
    }
    internal Task StartEffectAsync(Action<RgbEffectBuilder> configure)
    {
        var builder = new RgbEffectBuilder(factory); configure(builder);
        return StartEffectAsync(builder.Build());
    }
    internal Task StartEffectAsync(IRgbEffect effect)
    {
        effect.EnsureValid();
        lock (sync)
        {
            effect.Initialize(leds.Length, clock);
            completed = effect.Apply(leds);
            Volatile.Write(ref lastError, null);
            currentEffect = effect; lastFrame = clock.GetTimestamp();
        }
        return Task.CompletedTask;
    }
    internal void StopEffect()
    {
        lock (sync) { currentEffect = null; completed = true; leds.AsSpan().Clear(); }
    }
    internal void SetSpeed(TimeSpan speed) => UpdateEffect(effect => effect.Speed = speed);
    internal void UpdateEffect(Action<IRgbEffect> update)
    {
        lock (sync)
        {
            if (currentEffect is null) return;
            var candidate = currentEffect.Clone(); update(candidate); candidate.EnsureValid();
            candidate.Initialize(leds.Length, clock); currentEffect = candidate; completed = false;
        }
    }
    public RgbStatus GetStatus() { lock (sync) return new(currentEffect?.EffectType ?? RgbEffect.Off, currentEffect?.Speed ?? TimeSpan.FromMilliseconds(100), leds.Length, IsStreaming, LastError); }
    public Vector3[] GetSnapshot() { lock (sync) return (Vector3[])leds.Clone(); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10), clock);
        Volatile.Write(ref ready, 1);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                lock (sync)
                {
                    if (currentEffect is null || completed || clock.GetElapsedTime(lastFrame) < currentEffect.Speed) continue;
                    try { completed = currentEffect.Apply(leds); lastFrame = clock.GetTimestamp(); }
                    catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { Volatile.Write(ref lastError, "LED renderer failed"); logger.LogError(ex, "LED renderer failed"); currentEffect = null; leds.AsSpan().Clear(); IsStreaming = false; }
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels the renderer timer. */ }
        finally { Volatile.Write(ref ready, 0); StopEffect(); IsStreaming = false; }
    }
}

public sealed record RgbStatus(RgbEffect Effect, TimeSpan Speed, int LedCount, bool Streaming, string? LastError);
