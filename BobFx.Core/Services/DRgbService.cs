using BobFx.Core.Services.Effects;
using System.Numerics;

namespace BobFx.Core.Services
{
    public class DRgbService
    {
        private readonly ILogger<DRgbService> logger;
        private readonly IRgbEffectFactory effectFactory;
        private readonly Lock @lock = new();
        private CancellationTokenSource? cts;
        private IRgbEffect? currentEffect;
        private Task? effectTask;

        public Vector3[] Leds { get; private set; }
        public event Action? OnUpdate;

        public RgbEffect CurrentEffect => currentEffect?.EffectType ?? RgbEffect.Off;
        public TimeSpan Speed => currentEffect?.Speed ?? TimeSpan.FromMilliseconds(100);
        public IRgbEffect? CurrentEffectInstance => currentEffect;
        public int LedCount { get; private set; }
        public bool IsUdpActive { get; set; } = false;

        public DRgbService(int ledCount = 30, ILogger<DRgbService>? logger = null, IRgbEffectFactory? effectFactory = null)
        {
            this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DRgbService>.Instance;
            this.effectFactory = effectFactory ?? new RgbEffectFactory();
            LedCount = ledCount;
            Leds = new Vector3[ledCount];
            this.logger.LogInformation("DRgbService initialized with {LedCount} LEDs", ledCount);
        }

        public void SetLedCount(int newCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newCount);

            lock (@lock)
            {
                var newLeds = new Vector3[newCount];
                int copyCount = Math.Min(Leds.Length, newCount);
                Array.Copy(Leds, newLeds, copyCount);

                Leds = newLeds;
                LedCount = newCount;

                for (int i = copyCount; i < newCount; i++)
                {
                    Leds[i] = Vector3.Zero;
                }

                // Reinitialize current effect with new LED count
                currentEffect?.Initialize(newCount);

                logger.LogInformation("LED count set to {LedCount}", newCount);
            }
        }

        /// <summary>
        /// Start an effect using the effect instance directly.
        /// </summary>
        public async Task StartEffectAsync(IRgbEffect effect)
        {
            ArgumentNullException.ThrowIfNull(effect);

            // Validate the effect before starting
            var validation = effect.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                   $"Effect validation failed: {string.Join(", ", validation.Errors)}");
            }

            await StopAndWaitForCurrentEffect();

            lock (@lock)
            {
                currentEffect = effect;
                currentEffect.Initialize(LedCount);
                cts = new CancellationTokenSource();
                effectTask = RunEffectAsync(cts.Token);
                logger.LogInformation("Started effect {Effect} with speed {Speed}ms",
          effect.EffectType, effect.Speed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Start an effect using a builder.
        /// </summary>
        public async Task StartEffectAsync(Action<RgbEffectBuilder> configure)
        {
            var builder = new RgbEffectBuilder(effectFactory);
            configure(builder);
            var effect = builder.Build();
            await StartEffectAsync(effect);
        }

        /// <summary>
        /// Start an effect by type with default configuration.
        /// </summary>
        public async Task StartEffectAsync(RgbEffect effectType)
        {
            var effect = effectFactory.Create(effectType);
            await StartEffectAsync(effect);
        }

        public void StopEffect()
        {
            lock (@lock)
            {
                StopEffectInternal();
                logger.LogInformation("Stopped effect");
            }
        }

        public void SetSpeed(TimeSpan speed)
        {
            lock (@lock)
            {
                if (currentEffect != null)
                {
                    currentEffect.Speed = speed;
                    logger.LogInformation("Effect speed set to {Speed}ms", speed.TotalMilliseconds);
                }
            }
        }

        /// <summary>
        /// Update the current effect's properties while it's running.
        /// </summary>
        public void UpdateEffect(Action<IRgbEffect> update)
        {
            lock (@lock)
            {
                if (currentEffect != null)
                {
                    update(currentEffect);
                    logger.LogInformation("Effect {Effect} updated", currentEffect.EffectType);
                }
            }
        }

        private async Task StopAndWaitForCurrentEffect()
        {
            CancellationTokenSource? oldCts = null;
            Task? oldTask;

            lock (@lock)
            {
                if (cts != null)
                {
                    oldCts = cts;
                    cts.Cancel();
                }
                oldTask = effectTask;
                cts = null;
            }

            if (oldTask is not null)
            {
                try
                {
                    await oldTask;
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                finally
                {
                    oldCts?.Dispose();
                }
            }
        }

        private void StopEffectInternal()
        {
            cts?.Cancel();
        }

        private void ClearStrip()
        {
            for (int i = 0; i < LedCount; i++)
                Leds[i] = Vector3.Zero;
            OnUpdate?.Invoke();
        }

        private async Task RunEffectAsync(CancellationToken token)
        {
            bool wasCancelled = false;
            try
            {
                while (!token.IsCancellationRequested && currentEffect != null)
                {
                    bool completed;

                    lock (@lock)
                    {
                        // Apply the effect using the strategy pattern
                        completed = currentEffect.Apply(Leds.AsSpan(), token);
                    }

                    OnUpdate?.Invoke();

                    if (completed)
                    {
                        logger.LogInformation("Effect {Effect} completed", currentEffect.EffectType);
                        break;
                    }

                    await Task.Delay(currentEffect.Speed, token);
                }
            }
            catch (TaskCanceledException)
            {
                wasCancelled = true;
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
            finally
            {
                // Only clear the strip if the effect was cancelled, not if it completed naturally
                if (wasCancelled)
                {
                    ClearStrip();
                }
                else
                {
                    // Effect completed naturally, trigger one final update to show the final state
                    OnUpdate?.Invoke();
                }
            }
        }

        public byte[] ToByteArray()
        {
            byte[] data = new byte[Leds.Length * 3];

            lock (@lock)
            {
                for (int i = 0; i < Leds.Length; i++)
                {
                    int index = i * 3;
                    var (r, g, b) = ColorHelper.ToRgbBytes(Leds[i]);
                    data[index] = r;
                    data[index + 1] = g;
                    data[index + 2] = b;
                }
            }

            return data;
        }

        public void CopyTo(Span<byte> dest)
        {
            int required = Leds.Length * 3;
            if (dest.Length < required)
                throw new ArgumentException($"Destination span is too small. Required {required} bytes.", nameof(dest));

            lock (@lock)
            {
                for (int i = 0; i < Leds.Length; i++)
                {
                    int index = i * 3;
                    var (r, g, b) = ColorHelper.ToRgbBytes(Leds[i]);
                    dest[index] = r;
                    dest[index + 1] = g;
                    dest[index + 2] = b;
                }
            }
        }
    }
}
