using System.Security.Cryptography;
using System.Text;

namespace BobFx.Core.Services;

internal enum BusyBarScreen { Idle, Scanner, PlayerEdges, AllEdges, Go, Running, Flash, Dot }
internal sealed record BusyBarAnimation(string Name, byte[] Data);

/// <summary>Small native 72x16 animations for firmware 1.2.3 (bicycle0, BGR888).
/// Format reference: busy-app/busybar-firmware tag 1.2.3, lib/anim_file/anim_file_format.h.
/// Frames are generated locally; only uploaded assets and playback commands cross Wi-Fi.</summary>
internal static class BusyBarAnimations
{
    internal const int Fps = 30;
    internal static BusyBarScreen Screen(GamePhase phase, double elapsed, double preparation, AnimationSettings animation) => phase switch
    {
        GamePhase.PreCountdown => elapsed < preparation * 0.6 ? BusyBarScreen.Scanner
            : elapsed < preparation * 0.8 ? BusyBarScreen.PlayerEdges : BusyBarScreen.AllEdges,
        GamePhase.Running => elapsed < 0.75 ? BusyBarScreen.Go : BusyBarScreen.Running,
        GamePhase.Ending => elapsed < animation.FlashSeconds ? BusyBarScreen.Flash : BusyBarScreen.Dot,
        _ => BusyBarScreen.Idle
    };

    internal static Dictionary<BusyBarScreen, BusyBarAnimation> Create(bool showTimer, AnimationSettings timing) =>
        Enum.GetValues<BusyBarScreen>().ToDictionary(s => s, s => Create(s, showTimer, timing));

    private static BusyBarAnimation Create(BusyBarScreen screen, bool showTimer, AnimationSettings timing)
    {
        var seconds = screen switch
        {
            BusyBarScreen.Idle or BusyBarScreen.Running => 2d,
            BusyBarScreen.Scanner or BusyBarScreen.PlayerEdges or BusyBarScreen.AllEdges => 2d,
            BusyBarScreen.Flash => timing.FlashSeconds,
            // One complete lap loops without a discontinuity, regardless of configured speed.
            BusyBarScreen.Dot => 1 / timing.DotLapsPerSecond,
            _ => 1d / Fps
        };
        var count = Math.Max(1, (int)Math.Round(seconds * Fps));
        var frames = Enumerable.Range(0, count).Select(i => Render(screen, showTimer,
            screen == BusyBarScreen.Dot ? (double)i / count / timing.DotLapsPerSecond : (double)i / Fps, timing)).ToArray();
        var data = Encode(frames, screen == BusyBarScreen.Dot ? Math.Clamp((int)Math.Round(count * timing.DotLapsPerSecond), 1, 255) : Fps);
        var hash = Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();
        return new($"bobfx-{screen.ToString().ToLowerInvariant()}-{hash}.anim", data);
    }

    internal static byte[] Render(BusyBarScreen screen, bool showTimer, double elapsed, AnimationSettings timing)
    {
        var pixels = new byte[72 * 16 * 3];
        void Pixel(int x, int y, byte r, byte g, byte b)
        {
            if (x is < 0 or >= 72 || y is < 0 or >= 16) return;
            var offset = (y * 72 + x) * 3;
            pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r;
        }
        void Rainbow(int x, int y, double brightness = 1)
        {
            var position = x * Math.PI / 36 + y * Math.PI / 16 - elapsed * Math.PI;
            byte Channel(double offset) => (byte)((127.5 + 127.5 * Math.Sin(position + offset)) * brightness);
            Pixel(x, y, Channel(0), Channel(2.094), Channel(4.189));
        }
        void Blend(int x, int y, double opacity, bool shadow)
        {
            if (x is < 0 or >= 72 || y is < 0 or >= 16) return;
            var offset = (y * 72 + x) * 3;
            for (var channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (byte)(pixels[offset + channel] * (1 - opacity) + (shadow ? 0 : 255 * opacity));
        }
        void Text(string text, int y, int scale = 1, double opacity = 1)
        {
            var left = (72 - (text.Length * 6 - 1) * scale) / 2;
            var points = new List<(int X, int Y)>();
            for (var c = 0; c < text.Length; c++)
                for (var row = 0; row < 5; row++)
                    for (var col = 0; col < 5; col++)
                        if (Glyphs[text[c]][row * 5 + col] == '1')
                            for (var dy = 0; dy < scale; dy++)
                                for (var dx = 0; dx < scale; dx++)
                                {
                                    var x = left + (c * 6 + col) * scale + dx;
                                    points.Add((x, y + row * scale + dy));
                                }
            foreach (var (x, py) in points)
            {
                Blend(x - 1, py, opacity * 0.65, true); Blend(x + 1, py, opacity * 0.65, true);
                Blend(x, py - 1, opacity * 0.65, true); Blend(x, py + 1, opacity * 0.65, true);
            }
            foreach (var (x, py) in points) Blend(x, py, opacity, false);
        }
        if (screen is not (BusyBarScreen.Flash or BusyBarScreen.Dot or BusyBarScreen.Go))
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 72; x++) Rainbow(x, y, screen is BusyBarScreen.Scanner or BusyBarScreen.PlayerEdges or BusyBarScreen.AllEdges ? 0.5 : 0.8);
        if (screen == BusyBarScreen.Flash)
        {
            if ((int)(elapsed / timing.FlashSeconds * 6) % 2 == 0)
                for (var y = 0; y < 16; y++) for (var x = 0; x < 72; x++) Pixel(x, y, 255, 0, 0);
        }
        else if (screen == BusyBarScreen.Dot)
        {
            var p = (int)(elapsed * timing.DotLapsPerSecond * 172) % 172;
            var x = p < 72 ? p : p < 86 ? 71 : p < 158 ? 157 - p : 0;
            var y = p < 72 ? 0 : p < 86 ? p - 71 : p < 158 ? 15 : 172 - p;
            Pixel(x, y, 255, 0, 0);
        }
        else if (screen == BusyBarScreen.Go)
        {
            for (var y = 0; y < 16; y++) for (var x = 0; x < 72; x++) Pixel(x, y, 0, 180, 0);
            Text("GO!", 3, scale: 2);
        }
        else if (screen is BusyBarScreen.Scanner or BusyBarScreen.PlayerEdges or BusyBarScreen.AllEdges)
        {
            for (var x = 0; x < 72; x++)
            {
                var intensity = screen == BusyBarScreen.Scanner
                    ? (byte)(255 * Math.Max(0, 1 - Math.Abs(x - (Math.Sin(elapsed * Math.PI * 2 - Math.PI / 2) + 1) * 35.5) / 8)) : (byte)255;
                Pixel(x, 0, 0, (byte)(intensity / 3), intensity);
                Pixel(x, 15, 0, (byte)(intensity / 3), intensity);
            }
            if (screen == BusyBarScreen.AllEdges)
                for (var y = 0; y < 16; y++) { Pixel(0, y, 0, 80, 255); Pixel(71, y, 0, 80, 255); }
            // Leave the full center height available for the large live preparation number.
        }
        else
        {
            var opacity = (1 + Math.Cos(elapsed * Math.PI)) / 2;
            Text("BOB FX", screen == BusyBarScreen.Idle || showTimer ? 2 : 3,
                scale: screen == BusyBarScreen.Running && !showTimer ? 2 : 1, opacity: opacity);
            if (screen == BusyBarScreen.Idle) Text("IDLE", 9);
        }
        return pixels;
    }

    // Five rows of five pixels; kept independent of device font availability.
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        [' '] = "0000000000000000000000000", ['!'] = "0010000100001000000000100",
        ['B'] = "1111010001111101000111110", ['O'] = "0111010001100011000101110",
        ['F'] = "1111110000111101000010000", ['X'] = "1000101010001000101010001",
        ['I'] = "1111100100001000010011111", ['D'] = "1111010001100011000111110",
        ['L'] = "1000010000100001000011111", ['E'] = "1111110000111101000011111",
        ['P'] = "1111010001111101000010000", ['R'] = "1111010001111101001010001",
        ['C'] = "0111110000100001000001111", ['U'] = "1000110001100011000101110",
        ['N'] = "1000111001101011001110001", ['T'] = "1111100100001000010000100",
        ['G'] = "0111110000101111000101111"
    };

    internal static byte[] Encode(IReadOnlyList<byte[]> frames, int fps)
    {
        if (frames.Count == 0 || fps is < 1 or > 255 || frames.Any(f => f.Length != 72 * 16 * 3))
            throw new ArgumentException("Native animation needs full 72x16 BGR frames and a valid frame rate.");
        var encoded = frames.Select(Compress).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        const uint headerLength = 36, sectionLength = 21; // <8s BBBB BHB II III, then <IIIB + "default\0"
        writer.Write("bicycle0"u8); writer.Write((byte)0); writer.Write((byte)72); writer.Write((byte)16); writer.Write((byte)0);
        writer.Write((byte)fps); writer.Write((ushort)encoded.Max(f => f.Length)); writer.Write((byte)0);
        writer.Write(sectionLength); writer.Write((uint)encoded.Sum(f => 4 + f.Length));
        writer.Write(1u); writer.Write((uint)frames.Count); writer.Write((uint)frames.Count);
        writer.Write(0u); writer.Write((uint)(frames.Count - 1)); writer.Write(headerLength + sectionLength); writer.Write((byte)1);
        writer.Write("default\0"u8);
        for (var i = 0; i < frames.Count; i++)
        {
            var compressed = encoded[i].Length < frames[i].Length;
            var bytes = compressed ? encoded[i] : frames[i];
            writer.Write((byte)(compressed ? 1 : 0)); writer.Write((byte)1); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        return stream.ToArray();
    }

    private static byte[] Compress(byte[] pixels)
    {
        using var result = new MemoryStream();
        var count = pixels.Length / 3;
        bool Equal(int a, int b) => pixels.AsSpan(a * 3, 3).SequenceEqual(pixels.AsSpan(b * 3, 3));
        for (var i = 0; i < count;)
        {
            var run = 1;
            while (run < 127 && i + run < count && Equal(i, i + run)) run++;
            if (run >= 3)
            {
                result.WriteByte((byte)run); result.Write(pixels, i * 3, 3); i += run;
            }
            else
            {
                var start = i++;
                while (i - start < 127 && i < count && !(i + 2 < count && Equal(i, i + 1) && Equal(i, i + 2))) i++;
                result.WriteByte((byte)(0x80 | (i - start))); result.Write(pixels, start * 3, (i - start) * 3);
            }
        }
        var encoded = result.ToArray();
        return encoded.Length < pixels.Length ? encoded : pixels;
    }
}
