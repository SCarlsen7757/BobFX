using BobFx.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace BobFx.Core.Hubs;

public sealed class UiHub(UiStateService state) : Hub
{
    public const string PreviewGroup = "led-preview";
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
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.UnsubscribePreview(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
