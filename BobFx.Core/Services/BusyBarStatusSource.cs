using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace BobFx.Core.Services;

public interface IBusyBarStatusSource
{
    IAsyncEnumerable<byte[]> ReadAsync(BusyBarSettings bar, string password, CancellationToken token);
}

public sealed class BusyBarStatusSource : IBusyBarStatusSource
{
    public async IAsyncEnumerable<byte[]> ReadAsync(BusyBarSettings bar, string password, [EnumeratorCancellation] CancellationToken token)
    {
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        var uri = new Uri($"ws://{bar.Address}:{bar.Port}/api/status/ws?x-api-token={Uri.EscapeDataString(password)}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await ws.ConnectAsync(uri, deadline.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"enable\":true}"), WebSocketMessageType.Text, true, token);
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await ws.ReceiveAsync(buffer, token);
                if (received.MessageType == WebSocketMessageType.Close) yield break;
                if (message.Length + received.Count > 4 * 1024 * 1024) throw new InvalidDataException("Status frame too large.");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            if (received.MessageType == WebSocketMessageType.Binary) yield return message.ToArray();
        }
    }
}
