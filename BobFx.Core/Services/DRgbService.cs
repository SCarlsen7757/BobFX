using BobFx.Core.Services.Effects;
using Microsoft.Extensions.Options;
using System.Numerics;
namespace BobFx.Core.Services;

/// <summary>One hosted renderer owns the mutable frame; consumers receive copies.</summary>
public sealed class DRgbService : BackgroundService
{
    private readonly Lock sync = new();
    private readonly Vector3[] leds;
    private readonly IRgbEffectFactory factory;
    private readonly ILogger<DRgbService> logger;
    private readonly TimeProvider clock;
    private IRgbEffect? currentEffect;
    private bool completed;
    private long lastFrame;
    private int udpActive;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    private string? lastError;
    public string? LastError => Volatile.Read(ref lastError);
    public int LedCount => leds.Length;
    public bool IsUdpActive { get => Volatile.Read(ref udpActive) != 0; internal set => Volatile.Write(ref udpActive, value ? 1 : 0); }
    public RgbEffect CurrentEffect { get { lock (sync) return currentEffect?.EffectType ?? RgbEffect.Off; } }
    public TimeSpan Speed { get { lock (sync) return currentEffect?.Speed ?? TimeSpan.FromMilliseconds(100); } }
    public DRgbService(ILogger<DRgbService> logger, IOptions<WLedOptions> options, IRgbEffectFactory factory, TimeProvider clock)
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
        Validate(effect);
        lock (sync)
        {
            effect.Initialize(leds.Length, clock);
            completed = effect.Apply(leds, CancellationToken.None);
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
            var candidate = currentEffect.Clone(); update(candidate); Validate(candidate);
            candidate.Initialize(leds.Length); currentEffect = candidate; completed = false;
        }
    }
    public RgbStatus GetStatus() { lock (sync) return new(currentEffect?.EffectType ?? RgbEffect.Off, currentEffect?.Speed ?? TimeSpan.FromMilliseconds(100), leds.Length, IsUdpActive, LastError); }
    public Vector3[] GetSnapshot() { lock (sync) return (Vector3[])leds.Clone(); }
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < leds.Length * 3) throw new ArgumentException("Destination is too small.", nameof(destination));
        lock (sync)
            for (var i = 0; i < leds.Length; i++)
            {
                var (r, g, b) = ColorHelper.ToRgbBytes(leds[i]);
                destination[i * 3] = r; destination[i * 3 + 1] = g; destination[i * 3 + 2] = b;
            }
    }
    private static void Validate(IRgbEffect effect) => EffectValidation.EnsureValid(effect);
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
                    try { completed = currentEffect.Apply(leds, stoppingToken); lastFrame = clock.GetTimestamp(); }
                    catch (Exception ex) { Volatile.Write(ref lastError, "LED renderer failed"); logger.LogError(ex, "LED renderer failed"); currentEffect = null; leds.AsSpan().Clear(); IsUdpActive = false; }
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref ready, 0); StopEffect(); IsUdpActive = false; }
    }
}

public sealed record RgbStatus(RgbEffect Effect, TimeSpan Speed, int LedCount, bool Streaming, string? LastError);
