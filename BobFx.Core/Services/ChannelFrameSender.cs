using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace BobFx.Core.Services
{
    // Consumer: reads frames from the channel, sends via UdpClientService and returns pooled buffers.
    public class ChannelFrameSender : BackgroundService
    {
        private readonly Channel<(byte[] Buffer, int Length)> channel;
        private readonly UdpClientService udp;
        private readonly Microsoft.Extensions.Logging.ILogger<ChannelFrameSender> logger;

        public ChannelFrameSender(Channel<(byte[] Buffer, int Length)> channel, UdpClientService udp, Microsoft.Extensions.Logging.ILogger<ChannelFrameSender> logger)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.udp = udp ?? throw new ArgumentNullException(nameof(udp));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var reader = channel.Reader;
            var pool = ArrayPool<byte>.Shared;

            try
            {
                while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        try
                        {
                            await udp.SendAsync(item.Buffer.AsMemory(0, item.Length), stoppingToken).ConfigureAwait(false);
                            logger.LogDebug("Sent frame of {Len} bytes", item.Length);
                        }
                        catch (OperationCanceledException)
                        {
                            // Return buffer and exit
                            pool.Return(item.Buffer);
                            logger.LogInformation("Send cancelled during shutdown");
                            return;
                        }
                        catch
                        {
                            // Ignore send errors for now; return buffer to pool
                            logger.LogWarning("Error sending frame — buffer returned to pool");
                        }
                        finally
                        {
                            pool.Return(item.Buffer);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
