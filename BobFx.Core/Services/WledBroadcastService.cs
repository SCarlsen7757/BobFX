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
        private string? lastError;
        private long lastSendUtcTicks;
        public string? LastError => Volatile.Read(ref lastError);
        public DateTimeOffset? LastSendUtc => Volatile.Read(ref lastSendUtcTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        private readonly DRgbService rgbService;
        private readonly UdpClientService udpClient;
        private readonly ILogger<WLedBroadcastService> logger;
        private readonly ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;
        private readonly TimeSpan broadcastInterval;
        private readonly TimeProvider clock;
        private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(1);

        public WLedBroadcastService(ILogger<WLedBroadcastService> logger,
                                    IOptions<WLedOptions> udpOptions,
                                    DRgbService rgbService,
                                    UdpClientService udpClient, TimeProvider? clock = null)
        {
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));
            this.udpClient = udpClient ?? throw new ArgumentNullException(nameof(udpClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(udpOptions);

            this.clock = clock ?? TimeProvider.System;
            this.broadcastInterval = TimeSpan.FromMilliseconds(udpOptions.Value.Udp.UpdateIntervalMs);
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
                        await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stoppingToken);
                        continue;
                    }

                    var sent = await SendFrameAsync(stoppingToken);

                    // A failed transport retries at most once per second instead of flooding logs and the socket.
                    await Task.Delay(sent ? broadcastInterval : FailureRetryInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("WLED Broadcast Service stopped");
            }
            catch (Exception ex)
            {
                Volatile.Write(ref lastError, "WLED broadcast loop failed");
                logger.LogError(ex, "WLED Broadcast Service encountered an error");
            }
        }

        internal static int WriteFrame(DRgbService rgb, Span<byte> buffer)
        {
            int size = 2 + rgb.LedCount * 3;
            if (buffer.Length < size) throw new ArgumentException("Frame buffer is too small.", nameof(buffer));
            buffer[0] = 2; buffer[1] = 1;
            rgb.CopyTo(buffer.Slice(2, size - 2));
            return size;
        }

        private async Task<bool> SendFrameAsync(CancellationToken cancellationToken)
        {
            int rgbDataSize = rgbService.LedCount * 3;
            int totalSize = rgbDataSize + 2; // +2 for WLED DRGB protocol header

            // Rent a buffer from the pool
            byte[] buffer = bufferPool.Rent(totalSize);

            try
            {
                WriteFrame(rgbService, buffer.AsSpan(0, totalSize));

                // Send via UDP
                await udpClient.SendAsync(buffer.AsMemory(0, totalSize), cancellationToken);
                if (Interlocked.Exchange(ref lastError, null) is not null) logger.LogInformation("WLED UDP streaming recovered");
                Volatile.Write(ref lastSendUtcTicks, clock.GetUtcNow().UtcTicks);

                logger.LogTrace("Sent WLED frame: {Size} bytes ({LedCount} LEDs)", totalSize, rgbService.LedCount);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during shutdown
                throw;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref lastError, "UDP send failed") is null)
                    logger.LogWarning(ex, "WLED UDP send failed; retrying once per second");
                return false;
            }
            finally
            {
                // Always return buffer to the pool
                bufferPool.Return(buffer);
            }
        }
    }
}
