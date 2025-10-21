using System.Buffers;
using System.Threading.Channels;

namespace BobFx.Core.Services
{
    // Producer: generates frames and writes pooled buffers into the channel.
    public class FrameSender : BackgroundService
    {
        private readonly DRgbService rgbService;
        private readonly Channel<(byte[] Buffer, int Length)> channel;
        private readonly ILogger<FrameSender> logger;


        public FrameSender(DRgbService rgbService, Channel<(byte[] Buffer, int Length)> channel, ILogger<FrameSender> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.rgbService = rgbService ?? throw new ArgumentNullException(nameof(rgbService));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pool = ArrayPool<byte>.Shared;
            var writer = channel.Writer;

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    int required = rgbService.LedCount * 3;

                    // Rent a buffer of at least 'required' bytes
                    byte[] buffer = pool.Rent(required);

                    // Fill buffer
                    rgbService.CopyTo(buffer.AsSpan(0, required));

                    var item = (Buffer: buffer, Length: required);

                    // Try to write into the channel quickly. The channel is configured with DropOldest policy so
                    // newer frames will replace older ones when full (coalescing behaviour).
                    if (!writer.TryWrite(item))
                    {
                        logger.LogDebug("Channel full — attempting async write (frame size {Size})", required);
                        try
                        {
                            await writer.WriteAsync(item, stoppingToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Return buffer if cancelled
                            pool.Return(buffer);
                            logger.LogInformation("Frame write cancelled during shutdown");
                            break;
                        }
                        catch (Exception ex)
                        {
                            // If write failed for other reasons, return buffer
                            pool.Return(buffer);
                            logger.LogError(ex, "Failed to write frame to channel");
                        }
                    }
                    else
                    {
                        logger.LogDebug("Wrote frame to channel (size {Size})", required);
                    }

                    try
                    {
                        await Task.Delay(rgbService.Speed, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
