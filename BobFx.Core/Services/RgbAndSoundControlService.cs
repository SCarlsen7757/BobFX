using BobFx.Core.Services.Effects;
namespace BobFx.Core.Services;
public enum GamePhase { Idle, PreCountdown, Running, Ending }

/// <summary>Serializes game and manual commands and owns streaming transitions.</summary>
public sealed class RgbAndSoundControlService(
    CountdownService countdown, DRgbService rgb, SoundService sounds, TimeProvider clock,
    ILogger<RgbAndSoundControlService> logger, InstallationSettingsStore? settings = null) : BackgroundService
{
    private readonly SemaphoreSlim commands = new(1, 1);
    private int phase;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    private int shuttingDown;
    private long phaseStarted;
    private sealed record SceneState(GamePhase Phase, long Started, double Preparation);
    private SceneState scene = new(GamePhase.Idle, 0, 5);
    private bool switchedToRunning;
    private int locating;
    public bool IsLocating => Volatile.Read(ref locating) != 0;
    public double PhaseElapsedSeconds => clock.GetElapsedTime(phaseStarted).TotalSeconds;
    public double PreparationSeconds { get; private set; } = 5;
    public (GamePhase Phase, double Elapsed, double Preparation) GetSceneFrame()
    {
        var value = Volatile.Read(ref scene);
        return (value.Phase, clock.GetElapsedTime(value.Started).TotalSeconds, value.Preparation);
    }
    public async Task ConfigureAsync(Func<Task> action)
    {
        await commands.WaitAsync();
        try { EnsureManual(); if (rgb.IsUdpActive) throw new InvalidOperationException("Stop manual output before configuring devices."); await action(); }
        finally { commands.Release(); }
    }
    public async Task LocateAsync(Func<Task> action)
    {
        await commands.WaitAsync();
        try
        {
            EnsureManual();
            if (rgb.IsUdpActive) throw new InvalidOperationException("Stop manual output before locating a board.");
            Volatile.Write(ref locating, 1);
        }
        finally { commands.Release(); }
        try { await action(); }
        finally { Volatile.Write(ref locating, 0); }
    }
    public GamePhase Phase => (GamePhase)Volatile.Read(ref phase);
    private void SetPhase(GamePhase value)
    {
        phaseStarted = clock.GetTimestamp();
        Volatile.Write(ref scene, new(value, phaseStarted, PreparationSeconds));
        Volatile.Write(ref phase, (int)value);
    }
    public async Task StartGameAsync(bool withPreCountdown = true, TimeSpan? preDuration = null, TimeSpan? duration = null, TimeSpan? deviation = null)
    {
        await commands.WaitAsync();
        try
        {
            EnsureAvailable();
            if (IsLocating) throw new InvalidOperationException("A board is being located.");
            if (Phase is GamePhase.PreCountdown or GamePhase.Running) throw new InvalidOperationException("A game is already active.");
            countdown.Start(withPreCountdown, preDuration, duration, deviation);
            PreparationSeconds = countdown.IsPreCountdownRunning ? countdown.PreCountdownRemaining.TotalSeconds : 0;
            switchedToRunning = false;
            rgb.IsUdpActive = true;
            if (countdown.IsPreCountdownRunning)
            {
                SetPhase(GamePhase.PreCountdown);
                await rgb.StartEffectAsync(b => b.WithEffect(RgbEffect.FadeIn).WithColor("#0000ff").WithSpeed(10).WithFadeDuration(countdown.PreCountdownRemaining));
            }
            else await EnterRunningAsync();
        }
        finally { commands.Release(); }
    }
    public async Task StopGameAsync()
    {
        await commands.WaitAsync();
        try
        {
            EnsureAvailable();
            var previous = Phase;
            countdown.Stop();
            if (previous == GamePhase.Running) await EnterEndingAsync();
            else if (previous == GamePhase.PreCountdown) EnterIdle();
        }
        finally { commands.Release(); }
    }
    public async Task StartManualEffectAsync(Action<RgbEffectBuilder> configure)
    {
        await commands.WaitAsync();
        try { EnsureManual(); await rgb.StartEffectAsync(configure); rgb.IsUdpActive = true; }
        finally { commands.Release(); }
    }
    public async Task StopManualEffectAsync()
    {
        await commands.WaitAsync();
        try { EnsureManual(); EnterIdle(); }
        finally { commands.Release(); }
    }
    public async Task SetManualSpeedAsync(TimeSpan speed)
    {
        if (speed < TimeSpan.FromMilliseconds(10) || speed > TimeSpan.FromSeconds(60)) throw new ArgumentException("Speed must be 10–60000 milliseconds.");
        await commands.WaitAsync();
        try { EnsureManual(); rgb.SetSpeed(speed); }
        finally { commands.Release(); }
    }
    public async Task UpdateManualEffectAsync(Action<IRgbEffect> update)
    {
        await commands.WaitAsync();
        try { EnsureManual(); rgb.UpdateEffect(update); }
        finally { commands.Release(); }
    }
    private void EnsureAvailable()
    {
        if (Volatile.Read(ref shuttingDown) != 0) throw new InvalidOperationException("The application is shutting down.");
    }
    private void EnsureManual()
    {
        EnsureAvailable();
        if (IsLocating) throw new InvalidOperationException("A board is being located.");
        if (Phase != GamePhase.Idle) throw new InvalidOperationException("The game owns the lights until its ending sequence completes.");
    }
    private async Task EnterRunningAsync()
    {
        SetPhase(GamePhase.Running);
        switchedToRunning = false;
        await rgb.StartEffectAsync(b => b.WithEffect(RgbEffect.Blink).WithColor("#00ff00").WithColor("#a2ff00").WithSpeed(200));
    }
    private async Task EnterEndingAsync()
    {
        SetPhase(GamePhase.Ending);
        await rgb.StartEffectAsync(b => b.WithEffect(RgbEffect.Blink).WithColor("#ff0000").WithColor("#ffff00").WithSpeed(250));
        await sounds.Play("end");
    }
    private void EnterIdle()
    {
        rgb.IsUdpActive = false; rgb.StopEffect(); SetPhase(GamePhase.Idle);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), clock);
        Volatile.Write(ref ready, 1);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await commands.WaitAsync(stoppingToken);
                try
                {
                    countdown.Advance();
                    if (Phase == GamePhase.PreCountdown && countdown.IsRunning) await EnterRunningAsync();
                    else if (Phase == GamePhase.Running && !countdown.IsRunning) await EnterEndingAsync();
                    else if (Phase == GamePhase.Running && !switchedToRunning && clock.GetElapsedTime(phaseStarted) >= TimeSpan.FromSeconds(5))
                    {
                        await rgb.StartEffectAsync(b => b.WithEffect(RgbEffect.Twinkle).WithColor("#a2ff00"));
                        switchedToRunning = true;
                    }
                    else if (Phase == GamePhase.Ending && clock.GetElapsedTime(phaseStarted) >= TimeSpan.FromSeconds(settings is null ? 5 : settings.Read().Animation.FlashSeconds + settings.Read().Animation.DotSeconds)) EnterIdle();
                }
                finally { commands.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels the coordinator timer. */ }
        finally
        {
            Volatile.Write(ref ready, 0);
            await commands.WaitAsync();
            try { countdown.Stop(); EnterIdle(); }
            finally { commands.Release(); }
            logger.LogInformation("Game coordinator stopped");
        }
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref shuttingDown, 1);
        return base.StopAsync(cancellationToken);
    }
    public override void Dispose() { Volatile.Write(ref shuttingDown, 1); base.Dispose(); }
}
