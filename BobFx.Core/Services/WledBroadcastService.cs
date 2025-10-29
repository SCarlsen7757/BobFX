using System.Buffers;

namespace BobFx.Core.Services
{
    /// <summary>
    /// Background service that continuously broadcasts LED data to WLED controllers via UDP.
    /// Reads from DRgbService and sends WLED DRGB protocol packets over broadcast address.
    /// Sends at a fixed rate (~60 FPS) to prevent WLED timeout and ensure smooth display.
    /// </summary>
    public class WledBroadcastService : BackgroundService
    {
        private readonly DRgbService rgbService;
        private readonly UdpClientService udpClient;
        private readonly ILogger<WledBroadcastService> logger;
        private readonly ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;

        // Fixed broadcast interval for smooth WLED display (10ms = ~100 FPS)
        private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(16);

        public WledBroadcastService(
     DRgbService rgbService,
UdpClientService udpClient,
   ILogger<WledBroadcastService> logger)
        {
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));
            this.udpClient = udpClient ?? throw new ArgumentNullException(nameof(udpClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("WLED Broadcast Service started - Target: {Target}, Interval: {Interval}ms",
         udpClient.TargetEndpoint, BroadcastInterval.TotalMilliseconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Only send data when UDP is active
                    if (!rgbService.IsUdpActive)
                    {
                        await Task.Delay(100, stoppingToken);
                        continue;
                    }

                    await SendFrameAsync(stoppingToken);

                    // Always send at fixed interval for smooth display
                    await Task.Delay(BroadcastInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("WLED Broadcast Service stopped");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WLED Broadcast Service encountered an error");
            }
        }

        private async Task SendFrameAsync(CancellationToken cancellationToken)
        {
            int rgbDataSize = rgbService.LedCount * 3;
            int totalSize = rgbDataSize + 2; // +2 for WLED DRGB protocol header

            // Rent a buffer from the pool
            byte[] buffer = bufferPool.Rent(totalSize);

            try
            {
                // WLED DRGB Protocol Header:
                // Byte 0: Protocol/Mode (2 = DRGB - Direct RGB)
                // Byte 1: Timeout in seconds (2 = 2 seconds)
                buffer[0] = 2;
                buffer[1] = 2;

                // Copy RGB data from the service
                rgbService.CopyTo(buffer.AsSpan(2, rgbDataSize));

                // Send via UDP
                await udpClient.SendAsync(buffer.AsMemory(0, totalSize), cancellationToken);

                logger.LogTrace("Sent WLED frame: {Size} bytes ({LedCount} LEDs)", totalSize, rgbService.LedCount);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send WLED frame");
            }
            finally
            {
                // Always return buffer to the pool
                bufferPool.Return(buffer);
            }
        }
    }
}
