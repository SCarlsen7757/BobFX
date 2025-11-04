using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace BobFx.Core.Services
{
    public class UdpClientService : IDisposable
    {
        private readonly UdpClient udpClient;
        private readonly ILogger<UdpClientService> logger;

        public IPEndPoint TargetEndpoint { get; }

        public UdpClientService(ILogger<UdpClientService> logger,
                                IOptions<WLedOptions.UpdOptions> udpOptions)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(udpOptions);

            var targetAddress = udpOptions.Value.TargetAddress;
            var targetPort = udpOptions.Value.TargetPort;

            if (string.IsNullOrWhiteSpace(targetAddress))
                throw new ArgumentException("targetAddress cannot be null or empty", nameof(udpOptions));
            if (targetPort <= 0 || targetPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(udpOptions), "targetPort must be between 1 and 65535");

            try
            {
                // Create UDP client without binding to a specific port initially
                udpClient = new UdpClient
                {
                    // Enable broadcast before binding
                    EnableBroadcast = true
                };

                // Additional socket options for Docker compatibility
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                // Don't bind - let the OS choose the source port when sending
                // This works better in Docker containers

                TargetEndpoint = new IPEndPoint(IPAddress.Parse(targetAddress), targetPort);

                logger.LogInformation("UDP Client initialized for target {Address}:{Port}", targetAddress, targetPort);
                logger.LogInformation("Broadcast enabled: {Enabled}, Target endpoint: {Endpoint}",
                    udpClient.EnableBroadcast, TargetEndpoint);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize UDP client");
                throw;
            }
        }

        // True asynchronous send using Socket.SendToAsync
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty) return;

            try
            {
                await udpClient.Client.SendToAsync(data, SocketFlags.None, TargetEndpoint, cancellationToken).ConfigureAwait(false);
                logger.LogTrace("Sent {Count} bytes to {Endpoint}", data.Length, TargetEndpoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // expected on shutdown
                logger.LogDebug("Send cancelled");
                throw;
            }
            catch (SocketException ex)
            {
                logger.LogError(ex, "Socket error sending UDP frame to {Endpoint}. Error code: {ErrorCode}",
                    TargetEndpoint, ex.SocketErrorCode);
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
            ArgumentNullException.ThrowIfNull(data);
            return SendAsync(data.AsMemory(), cancellationToken);
        }

        public void Dispose()
        {
            try
            {
                logger.LogInformation("Disposing UDP client");
                udpClient?.Close();
                udpClient?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing UDP client");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
