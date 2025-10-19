using System.Numerics;

namespace BobFx.Core.Services
{
    public class DRgbService
    {

        private readonly Lock @lock = new();
        private CancellationTokenSource? cts;
        private RgbEffect currentEffect = RgbEffect.Off;
        private TimeSpan speed = TimeSpan.FromMilliseconds(100);
        private Task? effectTask;
        int direction = 1; // 1 = forward, -1 = backward

        public Vector3[] Leds { get; private set; }

        public event Action? OnUpdate;

        public RgbEffect CurrentEffect => currentEffect;
        public TimeSpan Speed => speed;

        public int LedCount { get; private set; }

        public Vector3 PrimaryColor { get; private set; } = new Vector3(1, 0, 0); //Red
        public Vector3 SecondaryColor { get; private set; } = new Vector3(1, 1, 1); //Black

        public DRgbService(int ledCount = 30)
        {
            this.LedCount = ledCount;
            Leds = new Vector3[ledCount];
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
            }
        }

        public void SetPrimaryColor(string color) => PrimaryColor = HexToRgb(color);

        public void SetSecondaryColor(string color) => SecondaryColor = HexToRgb(color);

        public async Task StartEffectAsync(RgbEffect effect, TimeSpan? speed = null)
        {
            if (currentEffect == effect)
            {
                if (speed is null) return;
                SetSpeed((TimeSpan)speed);
                return;
            }

            Task? oldTask = null;
            CancellationTokenSource? oldCts = null;

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
                catch (TaskCanceledException)
                {
                    // Expected, safe to ignore
                }
                catch (OperationCanceledException)
                {
                    // Also fine
                }
                finally
                {
                    oldCts?.Dispose();
                }
            }

            lock (@lock)
            {
                currentEffect = effect;
                if (speed is not null)
                {
                    this.speed = (TimeSpan)speed;
                }

                cts = new CancellationTokenSource();
                effectTask = RunEffectAsync(cts.Token);
            }
        }


        public void StopEffect()
        {
            lock (@lock)
            {
                StopEffectInternal();
            }
        }

        public void SetSpeed(TimeSpan speed)
        {
            lock (@lock)
            {
                this.speed = speed;
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
            int step = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    switch (currentEffect)
                    {
                        case RgbEffect.Rainbow:
                            ApplyRainbow(step);
                            step = (step + 4) % 360;
                            break;

                        case RgbEffect.Strobe:
                            ApplyStrobe(step);
                            step = (step + 1) % 2;
                            break;

                        case RgbEffect.Scanner:
                            ApplyScanner(step);
                            step += direction;

                            // Reverse direction at ends
                            if (step >= LedCount - 1)
                                direction = -1;
                            else if (step <= 0)
                                direction = 1;
                            break;

                        case RgbEffect.Off:
                        default:
                            ClearStrip();
                            break;
                    }

                    OnUpdate?.Invoke();
                    await Task.Delay(speed, token);
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                currentEffect = RgbEffect.Off;
                ClearStrip();
            }
        }

        // --- Effect implementations ---

        private void ApplyRainbow(int offset)
        {
            for (int i = 0; i < LedCount; i++)
            {
                int hue = (i * 360 / LedCount + offset) % 360;
                Leds[i] = HsvToRgb(hue, 1.0f, 1.0f);
            }
        }

        private void ApplyStrobe(int step)
        {
            Vector3 color = (step % 2 == 0) ? SecondaryColor : PrimaryColor;
            for (int i = 0; i < LedCount; i++)
                Leds[i] = color;
        }

        private void ApplyScanner(int step)
        {
            ClearStrip();
            int pos = step % LedCount;
            Leds[pos] = PrimaryColor;
        }

        public byte[] ToByteArray()
        {
            byte[] data = new byte[Leds.Length * 3];

            for (int i = 0; i < Leds.Length; i++)
            {
                int index = i * 3;
                data[index] = (byte)(Math.Clamp(Leds[i].X, 0f, 1f) * 255); // R
                data[index + 1] = (byte)(Math.Clamp(Leds[i].Y, 0f, 1f) * 255); // G
                data[index + 2] = (byte)(Math.Clamp(Leds[i].Z, 0f, 1f) * 255); // B
            }

            return data;
        }

        // --- Helper to convert HSV to RGB ---
        private static Vector3 HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1 - MathF.Abs((h / 60) % 2 - 1));
            float m = v - c;
            float r1, g1, b1;

            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            return new Vector3(r1 + m, g1 + m, b1 + m);
        }

        public static Vector3 HexToRgb(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                throw new ArgumentException("Hex color cannot be null or empty", nameof(hex));

            hex = hex.TrimStart('#');

            if (hex.Length != 6)
                throw new ArgumentException("Hex color must be 6 characters long", nameof(hex));

            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);

            // Normalize to 0..1
            return new Vector3(r / 255f, g / 255f, b / 255f);
        }

        public static string RgbToHex(Vector3 color)
        {
            int r = (int)(Math.Clamp(color.X, 0f, 1f) * 255);
            int g = (int)(Math.Clamp(color.Y, 0f, 1f) * 255);
            int b = (int)(Math.Clamp(color.Z, 0f, 1f) * 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }
}
