using BobFx.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace BobFx.Core.Hubs;

/// <summary>What the server tells the browsers. The method names are the event names the frontend listens for,
/// in frontend/src/lib/session.ts.</summary>
public interface IUiClient
{
    Task GameStateChanged(Versioned<GameView> game);
    Task EffectStateChanged(Versioned<EffectView> effect);
    Task DevicesChanged(Versioned<DiscoveryView> discovery);
    Task InstallationChanged(Versioned<InstallationView> installation);
    Task SoundRequested(SoundEvent sound);
    /// <summary>The virtual strip of the manual effect, one #RRGGBB per LED.</summary>
    Task LedPreviewChanged(string[] pixels);
    Task BoardFramesChanged(BoardFrame[] frames);
}

public sealed class UiHub(UiStateService state, BoardMonitorService boards) : Hub<IUiClient>
{
    public const string PreviewGroup = "led-preview";
    public const string BoardsGroup = "live-boards";
    public async Task SubscribePreview()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, PreviewGroup);
        state.SubscribePreview(Context.ConnectionId);
    }
    public async Task UnsubscribePreview()
    {
        state.UnsubscribePreview(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PreviewGroup);
    }
    /// <summary>Starts the live board frames for this browser and returns the current picture.</summary>
    public async Task<BoardFrame[]> SubscribeBoards()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, BoardsGroup);
        boards.Subscribe(Context.ConnectionId);
        return boards.Snapshot();
    }
    public async Task UnsubscribeBoards()
    {
        boards.Unsubscribe(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BoardsGroup);
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.UnsubscribePreview(Context.ConnectionId);
        boards.Unsubscribe(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
