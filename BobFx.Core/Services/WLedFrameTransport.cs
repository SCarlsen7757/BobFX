using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BobFx.Core.Services;

public interface IWLedFrameTransport
{
    Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token);
}

public sealed class WLedFrameTransport : IWLedFrameTransport, IDisposable
{
    private readonly UdpClient udp = new(AddressFamily.InterNetwork);
    // A frame goes to the same few controllers many times a second; their addresses are parsed once.
    private readonly ConcurrentDictionary<(string Address, int Port), IPEndPoint> targets = new();
    public async Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        var target = targets.GetOrAdd((device.Address, device.UdpPort), static key => new IPEndPoint(IPAddress.Parse(key.Address), key.Port));
        foreach (var packet in InstallationOutputService.Packets(pixels)) await udp.SendAsync(packet, target, token);
    }
    public void Dispose() => udp.Dispose();
}
