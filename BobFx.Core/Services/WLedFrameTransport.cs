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
        foreach (var packet in Packets(pixels)) await udp.SendAsync(packet, target, token);
    }
    /// <summary>A frame as WLED's DNRGB realtime packets: RGB bytes from a start index, which lets a strip
    /// longer than one packet be sent in pieces.</summary>
    public static IEnumerable<byte[]> Packets(byte[] pixels)
    {
        // At most 489 pixels per packet, so that it is never fragmented on the way.
        const int pixelsPerPacket = 489;
        for (var start = 0; start < pixels.Length / 3; start += pixelsPerPacket)
        {
            var length = Math.Min(pixelsPerPacket, pixels.Length / 3 - start) * 3;
            var packet = new byte[length + 4];
            // Protocol 4 is DNRGB; then a one-second hold after the last packet, and the start index.
            packet[0] = 4; packet[1] = 1; packet[2] = (byte)(start >> 8); packet[3] = (byte)start;
            pixels.AsSpan(start * 3, length).CopyTo(packet.AsSpan(4));
            yield return packet;
        }
    }
    public void Dispose() => udp.Dispose();
}
