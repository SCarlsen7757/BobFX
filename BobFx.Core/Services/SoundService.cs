namespace BobFx.Core.Services;

/// <summary>Publishes sound requests without retaining browser circuits.</summary>
public sealed class SoundService(ILogger<SoundService> logger)
{
    public event Action<string>? SoundRequested;

    public Task Play(string soundKey)
    {
        var path = soundKey switch
        {
            "end" => "/sounds/game_over/game-over-yeah.mp3",
            _ => throw new ArgumentException("Unknown sound key.", nameof(soundKey))
        };
        foreach (var subscriber in SoundRequested?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)subscriber)(path); }
            catch (Exception ex) { logger.LogWarning(ex, "Sound subscriber failed"); }
        }
        return Task.CompletedTask;
    }
}
