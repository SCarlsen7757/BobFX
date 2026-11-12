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
    private int locating;
    public bool IsLocating => Volatile.Read(ref locating) != 0;
    private int calibrating;
    public bool IsCalibrating => Volatile.Read(ref calibrating) != 0;
    private int testingLights;
    public bool IsTestingLights => Volatile.Read(ref testingLights) != 0;
    /// <summary>Starts, replaces or stops a light show test. A test owns the boards like calibration does,
    /// but a new test may replace a running one. <paramref name="action"/> returns whether a test is left playing.</summary>
    public async Task LightTestCommandAsync(Func<bool> action, bool starting = false)
    {
        await commands.WaitAsync();
        try
        {
            if (starting)
            {
                EnsureAvailable();
                if (IsLocating) throw new InvalidOperationException("A board is being located.");
                if (IsCalibrating) throw new InvalidOperationException("Board calibration owns the lights.");
                if (Phase != GamePhase.Idle) throw new InvalidOperationException("The game owns the lights until its ending sequence completes.");
                if (rgb.IsUdpActive) throw new InvalidOperationException("Stop manual output before testing the light show.");
            }
            Volatile.Write(ref testingLights, action() ? 1 : 0);
        }
        finally { commands.Release(); }
    }
    public async Task CalibrationCommandAsync(Func<Task> action, bool starting = false, bool stopping = false)
    {
        await commands.WaitAsync();
        try
        {
            if (starting)
            {
                EnsureManual();
                if (rgb.IsUdpActive) throw new InvalidOperationException("Stop manual output before calibration.");
            }
            else if (!IsCalibrating) throw new InvalidOperationException("Calibration has ended. Start calibration again.");
            await action();
            Volatile.Write(ref calibrating, stopping ? 0 : 1);
        }
        finally { commands.Release(); }
    }
    public double PhaseElapsedSeconds => clock.GetElapsedTime(phaseStarted).TotalSeconds;
    public double PreparationSeconds { get; private set; } = CountdownService.PreCountdownDuration.TotalSeconds;
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
    public Task StartGameAsync(TimeSpan? duration = null, TimeSpan? deviation = null) => StartGameAsync(true, duration, deviation);
    /// <param name="withPreCountdown">False only in tests that need a game already running.</param>
    internal async Task StartGameAsync(bool withPreCountdown, TimeSpan? duration = null, TimeSpan? deviation = null)
    {
        await commands.WaitAsync();
        try
        {
            EnsureAvailable();
            if (IsLocating) throw new InvalidOperationException("A board is being located.");
            if (IsCalibrating) throw new InvalidOperationException("Board calibration owns the lights.");
            if (IsTestingLights) throw new InvalidOperationException(LightTestMessage);
            if (Phase is GamePhase.PreCountdown or GamePhase.Running) throw new InvalidOperationException("A game is already active.");
            countdown.Start(withPreCountdown, duration, deviation);
            PreparationSeconds = countdown.IsPreCountdownRunning ? countdown.PreCountdownRemaining.TotalSeconds : 0;
            // The board show is rendered from the scene clock; a manual effect must not run on through the game.
            rgb.StopEffect();
            rgb.IsUdpActive = true;
            SetPhase(countdown.IsPreCountdownRunning ? GamePhase.PreCountdown : GamePhase.Running);
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
        if (IsCalibrating) throw new InvalidOperationException("Board calibration owns the lights.");
        if (IsTestingLights) throw new InvalidOperationException(LightTestMessage);
        if (Phase != GamePhase.Idle) throw new InvalidOperationException("The game owns the lights until its ending sequence completes.");
    }
    private const string LightTestMessage = "A light show test is playing on the boards.";
    private Task EnterEndingAsync()
    {
        SetPhase(GamePhase.Ending);
        return sounds.Play("end");
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
                    if (Phase == GamePhase.PreCountdown && countdown.IsRunning) SetPhase(GamePhase.Running);
                    else if (Phase == GamePhase.Running && !countdown.IsRunning) await EnterEndingAsync();
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
