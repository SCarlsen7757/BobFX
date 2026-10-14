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
    public async Task SendAsync(DeviceSettings device, byte[] pixels, CancellationToken token)
    {
        var target = new IPEndPoint(IPAddress.Parse(device.Address), device.UdpPort);
        foreach (var packet in InstallationOutputService.Packets(pixels)) await udp.SendAsync(packet, target, token);
    }
    public void Dispose() => udp.Dispose();
}
