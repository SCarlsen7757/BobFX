using Microsoft.Extensions.Options;
using System.Buffers;

namespace BobFx.Core.Services
{
    /// <summary>
    /// Background service that continuously broadcasts LED data to WLED controllers via UDP.
    /// Reads from DRgbService and sends WLED DRGB protocol packets over broadcast address.
    /// Sends at a configurable rate to prevent WLED timeout and ensure smooth display.
    /// </summary>
    public class WLedBroadcastService : BackgroundService
    {
        private readonly DRgbService rgbService;
        private readonly UdpClientService udpClient;
        private readonly ILogger<WLedBroadcastService> logger;
        private readonly ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;
        private readonly TimeSpan broadcastInterval;

        public WLedBroadcastService(ILogger<WLedBroadcastService> logger,
                                    IOptionsMonitor<WLedOptions.UpdOptions> udpOptions,
                                    DRgbService rgbService,
                                    UdpClientService udpClient)
        {
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));
            this.udpClient = udpClient ?? throw new ArgumentNullException(nameof(udpClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(udpOptions);

            this.broadcastInterval = udpOptions.CurrentValue.UpdateInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var fps = (int)(1000.0 / broadcastInterval.TotalMilliseconds);
            logger.LogInformation("WLED Broadcast Service started - Target: {Target}, Interval: {Interval}ms ({Fps} FPS)",
                udpClient.TargetEndpoint, broadcastInterval.TotalMilliseconds, fps);

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

                    // Send at configured interval for smooth display
                    await Task.Delay(broadcastInterval, stoppingToken);
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
                // Byte 1: Timeout in seconds (1 = 1 seconds)
                buffer[0] = 2;
                buffer[1] = 1;

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
