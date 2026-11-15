using BobFx.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace BobFx.Core.Hubs;

public sealed class UiHub(UiStateService state, BoardMonitorService boards) : Hub
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
