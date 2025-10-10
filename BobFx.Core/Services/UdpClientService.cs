using System.Net;
using System.Net.Sockets;

namespace BobFx.Core.Services
{
    public class UdpClientService(string targetAddress, int targetPort) : IDisposable
    {
        private readonly UdpClient udpClient = new();
        private readonly CancellationTokenSource cts = new();

        public IPEndPoint TargetEndpoint { get; } = new IPEndPoint(IPAddress.Parse(targetAddress), targetPort);

        public Task SendAsync(byte[] data)
        {
            return udpClient.SendAsync(data, data.Length, TargetEndpoint);
        }

        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
            GC.SuppressFinalize(this);
        }

    }
}
