using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BobFx.Core.Services
{
    public class UdpClientService : IDisposable
    {
        private readonly UdpClient udpClient;
        private readonly Microsoft.Extensions.Logging.ILogger<UdpClientService> logger;

        public IPEndPoint TargetEndpoint { get; }

        public UdpClientService(string targetAddress, int targetPort, Microsoft.Extensions.Logging.ILogger<UdpClientService> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(targetAddress))
                throw new ArgumentException("targetAddress cannot be null or empty", nameof(targetAddress));

            udpClient = new UdpClient(AddressFamily.InterNetwork);

            // Allow sending to broadcast addresses
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

            // Bind to any available local port so the socket is usable for sending
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            TargetEndpoint = new IPEndPoint(IPAddress.Parse(targetAddress), targetPort);
        }

        // True asynchronous send using Socket.SendToAsync
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty) return;

            // Socket.SendToAsync supports Memory<byte> directly and returns a ValueTask<int> in modern runtimes.
            try
            {
                await udpClient.Client.SendToAsync(data, SocketFlags.None, TargetEndpoint, cancellationToken).ConfigureAwait(false);
                logger.LogDebug("Sent {Count} bytes to {Endpoint}", data.Length, TargetEndpoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // expected on shutdown
                logger.LogInformation("Send cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending UDP frame to {Endpoint}", TargetEndpoint);
                throw;
            }
        }

        // Backwards-compatible overload
        public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            return SendAsync(data.AsMemory(), cancellationToken);
        }

        public void Dispose()
        {
            try
            {
                udpClient?.Close();
                udpClient?.Dispose();
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
