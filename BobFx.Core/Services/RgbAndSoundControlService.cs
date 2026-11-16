using BobFx.Core.Services.Effects;
namespace BobFx.Core.Services;
public enum GamePhase { Idle, PreCountdown, Running, Ending }

/// <summary>Serializes game and manual commands and owns streaming transitions.</summary>
public sealed class RgbAndSoundControlService(
    CountdownService countdown, DRgbService rgb, SoundService sounds, TimeProvider clock,
    ILogger<RgbAndSoundControlService> logger, InstallationSettingsStore settings) : BackgroundService
{
    private readonly SemaphoreSlim commands = new(1, 1);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    private int shuttingDown;
    /// <summary>The phase, when it began and how long the preparation of this game is: all that the light show
    /// and the BUSY Bar screens are drawn from. Replaced as a whole, so a reader never sees a mix of two phases.</summary>
    private sealed record SceneState(GamePhase Phase, long Started, double Preparation);
    private SceneState scene = new(GamePhase.Idle, 0, CountdownService.PreCountdownDuration.TotalSeconds);
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
            if (starting) EnsureIdle(before: "testing the light show", replacingLightTest: true);
            Volatile.Write(ref testingLights, action() ? 1 : 0);
        }
        finally { commands.Release(); }
    }
    public async Task CalibrationCommandAsync(Func<Task> action, bool starting = false, bool stopping = false)
    {
        await commands.WaitAsync();
        try
        {
            if (starting) EnsureIdle(before: "calibration");
            else if (!IsCalibrating) throw new InvalidOperationException("Calibration has ended. Start calibration again.");
            await action();
            Volatile.Write(ref calibrating, stopping ? 0 : 1);
        }
        finally { commands.Release(); }
    }
    public (GamePhase Phase, double Elapsed, double Preparation) GetSceneFrame()
    {
        var value = Volatile.Read(ref scene);
        return (value.Phase, clock.GetElapsedTime(value.Started).TotalSeconds, value.Preparation);
    }
    public async Task ConfigureAsync(Func<Task> action)
    {
        await commands.WaitAsync();
        try { EnsureIdle(before: "configuring devices"); await action(); }
        finally { commands.Release(); }
    }
    public async Task LocateAsync(Func<Task> action)
    {
        await commands.WaitAsync();
        try
        {
            EnsureIdle(before: "locating a board");
            Volatile.Write(ref locating, 1);
        }
        finally { commands.Release(); }
        try { await action(); }
        finally { Volatile.Write(ref locating, 0); }
    }
    public GamePhase Phase => Volatile.Read(ref scene).Phase;
    private void SetPhase(GamePhase value, double? preparation = null) =>
        Volatile.Write(ref scene, new(value, clock.GetTimestamp(), preparation ?? scene.Preparation));
    public Task StartGameAsync(TimeSpan? duration = null, TimeSpan? deviation = null) => StartGameAsync(true, duration, deviation);
    /// <param name="withPreCountdown">False only in tests that need a game already running.</param>
    internal async Task StartGameAsync(bool withPreCountdown, TimeSpan? duration = null, TimeSpan? deviation = null)
    {
        await commands.WaitAsync();
        try
        {
            // A game may start during the ending of the last one and replaces a manual effect.
            EnsureNotHeld();
            if (Phase is GamePhase.PreCountdown or GamePhase.Running) throw new InvalidOperationException("A game is already active.");
            countdown.Start(withPreCountdown, duration, deviation);
            // The board show is rendered from the scene clock; a manual effect must not run on through the game.
            rgb.StopEffect();
            rgb.IsUdpActive = true;
            SetPhase(countdown.IsPreCountdownRunning ? GamePhase.PreCountdown : GamePhase.Running,
                countdown.IsPreCountdownRunning ? countdown.PreCountdownRemaining.TotalSeconds : 0);
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
        try { EnsureIdle(); await rgb.StartEffectAsync(configure); rgb.IsUdpActive = true; }
        finally { commands.Release(); }
    }
    public async Task StopManualEffectAsync()
    {
        await commands.WaitAsync();
        try { EnsureIdle(); EnterIdle(); }
        finally { commands.Release(); }
    }
    public async Task SetManualSpeedAsync(TimeSpan speed)
    {
        await commands.WaitAsync();
        try { EnsureIdle(); rgb.SetSpeed(speed); }
        finally { commands.Release(); }
    }
    public async Task UpdateManualEffectAsync(Action<IRgbEffect> update)
    {
        await commands.WaitAsync();
        try { EnsureIdle(); rgb.UpdateEffect(update); }
        finally { commands.Release(); }
    }
    private void EnsureAvailable()
    {
        if (Volatile.Read(ref shuttingDown) != 0) throw new InvalidOperationException("The application is shutting down.");
    }
    /// <summary>Refuses while a locate, a calibration or a light show test holds the lights.</summary>
    /// <param name="replacingLightTest">A new light show test takes over from a running one.</param>
    private void EnsureNotHeld(bool replacingLightTest = false)
    {
        EnsureAvailable();
        if (IsLocating) throw new InvalidOperationException("A board is being located.");
        if (IsCalibrating) throw new InvalidOperationException("Board calibration owns the lights.");
        if (IsTestingLights && !replacingLightTest) throw new InvalidOperationException("A light show test is playing on the boards.");
    }
    /// <summary>Refuses unless nothing holds the lights and no game is on, its ending included.</summary>
    /// <param name="before">What is about to happen, for a command that a manual effect must be stopped for.
    /// The manual effect commands themselves leave it out.</param>
    private void EnsureIdle(string? before = null, bool replacingLightTest = false)
    {
        EnsureNotHeld(replacingLightTest);
        if (Phase != GamePhase.Idle) throw new InvalidOperationException("The game owns the lights until its ending sequence completes.");
        if (before is not null && rgb.IsUdpActive) throw new InvalidOperationException($"Stop manual output before {before}.");
    }

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
                    else if (Phase == GamePhase.Ending && clock.GetElapsedTime(scene.Started).TotalSeconds >= ShowTiming.EndingSeconds(settings.Snapshot.Animation)) EnterIdle();
                }
                finally { commands.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Host shutdown cancels the coordinator timer. */ }
        finally
        {
            Volatile.Write(ref ready, 0);
            // A command that is stuck on an unreachable device must not keep the host from stopping.
            var held = await commands.WaitAsync(ShutdownWait);
            if (!held) logger.LogWarning("A command was still running at shutdown; the lights are released without waiting for it");
            try { countdown.Stop(); EnterIdle(); }
            finally { if (held) commands.Release(); }
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
