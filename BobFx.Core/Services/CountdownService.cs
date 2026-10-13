using Microsoft.Extensions.Options;
namespace BobFx.Core.Services;

public sealed record CountdownSnapshot(bool IsRunning, bool IsPreCountdownRunning, TimeSpan Remaining, TimeSpan PreCountdownRemaining, long RunId,
    DateTimeOffset? StartedUtc = null, DateTimeOffset? EndedUtc = null);

/// <summary>State advanced by the single coordinator, without detached timer tasks.</summary>
public sealed class CountdownService(ILogger<CountdownService> logger, IOptions<CountdownOptions> options, TimeProvider clock)
{
    private CountdownSnapshot snapshot = new(false, false, TimeSpan.Zero, TimeSpan.Zero, 0);
    private long startedAt;
    private TimeSpan phaseDuration;
    private TimeSpan gameDuration;
    private long lastNotifiedSecond = -1;
    public event Action? OnTick;
    public event Action<TimeSpan>? OnStartOfEvent;
    public event Action? OnEndOfEvent;
    public event Action<TimeSpan>? OnPreCountdown;
    public CountdownSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool IsRunning => Snapshot.IsRunning;
    public bool IsPreCountdownRunning => Snapshot.IsPreCountdownRunning;
    public TimeSpan Remaining => Snapshot.Remaining;
    public TimeSpan PreCountdownRemaining => Snapshot.PreCountdownRemaining;
    public TimeSpan PreCountdownDuration => TimeSpan.FromSeconds(options.Value.PreDurationSeconds);
    public TimeSpan CountdownDuration => TimeSpan.FromSeconds(options.Value.DurationSeconds);
    public TimeSpan CountdownDeviation => TimeSpan.FromSeconds(options.Value.DeviationSeconds);

    internal void Start(bool withPre, TimeSpan? pre = null, TimeSpan? duration = null, TimeSpan? deviation = null)
    {
        var preDuration = pre ?? PreCountdownDuration;
        var baseDuration = duration ?? CountdownDuration;
        var variation = deviation ?? CountdownDeviation;
        if (preDuration < TimeSpan.Zero || preDuration > TimeSpan.FromMinutes(1) ||
            baseDuration < TimeSpan.FromSeconds(1) || baseDuration > TimeSpan.FromHours(24) ||
            variation < TimeSpan.Zero || variation >= baseDuration || variation.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Use a pre-countdown of 0–60 seconds, duration of 1 second–24 hours, and whole-second deviation smaller than duration.");
        var seconds = (int)variation.TotalSeconds;
        gameDuration = baseDuration + TimeSpan.FromSeconds(Random.Shared.Next(-seconds, seconds + 1));
        startedAt = clock.GetTimestamp();
        var preActive = withPre && preDuration > TimeSpan.Zero;
        phaseDuration = preActive ? preDuration : gameDuration;
        Volatile.Write(ref snapshot, new(!preActive, preActive, preActive ? TimeSpan.Zero : gameDuration,
            preActive ? preDuration : TimeSpan.Zero, Snapshot.RunId + 1, preActive ? null : clock.GetUtcNow()));
        lastNotifiedSecond = -1;
        if (preActive) Notify(OnPreCountdown, preDuration); else Notify(OnStartOfEvent, gameDuration);
        Notify(OnTick);
    }

    internal void Advance()
    {
        var state = Snapshot;
        if (!state.IsRunning && !state.IsPreCountdownRunning) return;
        var elapsed = clock.GetElapsedTime(startedAt);
        var remaining = phaseDuration - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            if (state.IsPreCountdownRunning)
            {
                // Preserve scheduler overshoot instead of extending the game.
                var overshoot = elapsed - phaseDuration;
                startedAt = clock.GetTimestamp();
                phaseDuration = gameDuration - overshoot;
                Volatile.Write(ref snapshot, state with { IsPreCountdownRunning = false, IsRunning = true,
                    PreCountdownRemaining = TimeSpan.Zero, Remaining = TimeSpan.FromTicks(Math.Max(0, phaseDuration.Ticks)),
                    StartedUtc = clock.GetUtcNow() - overshoot });
                lastNotifiedSecond = -1;
                Notify(OnStartOfEvent, gameDuration);
            }
            else
            {
                Volatile.Write(ref snapshot, state with { IsRunning = false, Remaining = TimeSpan.Zero, EndedUtc = clock.GetUtcNow() + remaining });
                Notify(OnEndOfEvent);
            }
            Notify(OnTick);
            return;
        }
        Volatile.Write(ref snapshot, state.IsPreCountdownRunning ? state with { PreCountdownRemaining = remaining } : state with { Remaining = remaining });
        var second = (long)Math.Ceiling(remaining.TotalSeconds);
        if (second != lastNotifiedSecond) { lastNotifiedSecond = second; Notify(OnTick); }
    }
    internal void Stop()
    {
        var old = Snapshot;
        Volatile.Write(ref snapshot, old with { IsRunning = false, IsPreCountdownRunning = false, Remaining = TimeSpan.Zero,
            PreCountdownRemaining = TimeSpan.Zero, EndedUtc = old.IsRunning ? clock.GetUtcNow() : old.EndedUtc });
        if (old.IsRunning) Notify(OnEndOfEvent);
        Notify(OnTick);
    }
    private void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { logger.LogWarning(ex, "Countdown subscriber failed"); }
    }
    private void Notify(Action<TimeSpan>? handlers, TimeSpan value)
    {
        if (handlers is null) return;
        foreach (Action<TimeSpan> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) when (ExceptionPolicy.IsRecoverable(ex)) { logger.LogWarning(ex, "Countdown subscriber failed"); }
    }
}
