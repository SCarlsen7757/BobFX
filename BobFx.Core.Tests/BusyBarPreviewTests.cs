using BobFx.Core.Services;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace BobFx.Core.Tests;

/// <summary>Lets the BUSY Bar art be judged without the hardware. Does nothing in a normal test run;
/// set BOBFX_BUSYBAR_PREVIEW to a directory to get a contact sheet per screen and preview.html,
/// a player that shows every screen as an LED matrix at its real frame rate.</summary>
public sealed class BusyBarPreviewTests
{
    private const int Width = 72, Height = 16, Cell = 8, Gap = 4, SheetFrames = 16;

    [Fact]
    public void WritesContactSheetsAndPlayerWhenRequested()
    {
        var directory = Environment.GetEnvironmentVariable("BOBFX_BUSYBAR_PREVIEW");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var timing = new AnimationSettings();
        var preparation = (int)CountdownService.PreCountdownDuration.TotalSeconds;
        var assets = BusyBarAnimations.Create(timing, BusyBarAnimFormat.Bicycle1, preparation);
        var script = new StringBuilder("const screens = [\n");
        foreach (var screen in Enum.GetValues<BusyBarScreen>())
        {
            var frames = BusyBarAnimationTests.Frames(screen, timing, BusyBarAnimFormat.Bicycle1, preparation);
            var shown = Enumerable.Range(0, Math.Min(SheetFrames, frames.Length)).Select(i => frames[i * frames.Length / Math.Min(SheetFrames, frames.Length)]).ToArray();
            File.WriteAllBytes(Path.Combine(directory, $"{screen.ToString().ToLowerInvariant()}.png"), Sheet(shown));
            var fps = assets[screen].Data[16]; // bicycle1 header: playback rate
            script.Append($"  {{ name: '{screen}', fps: {fps}, once: {(BusyBarAnimations.PlaysOnce(screen) ? "true" : "false")}, bytes: {assets[screen].Data.Length}, ")
                .Append($"frames: '{Convert.ToBase64String(frames.SelectMany(f => f).ToArray())}' }},\n");
        }
        File.WriteAllText(Path.Combine(directory, "preview.html"), Player.Replace("/*SCREENS*/", script.Append("];").ToString()));
    }

    /// <summary>Frames stacked top to bottom, each pixel drawn as a square LED on black.</summary>
    private static byte[] Sheet(byte[][] frames)
    {
        int width = Width * Cell, frameHeight = Height * Cell, height = frames.Length * (frameHeight + Gap) - Gap;
        var rgb = new byte[width * height * 3];
        for (var f = 0; f < frames.Length; f++)
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                    for (var dy = 0; dy < Cell - 1; dy++)
                        for (var dx = 0; dx < Cell - 1; dx++)
                        {
                            var target = ((f * (frameHeight + Gap) + y * Cell + dy) * width + x * Cell + dx) * 3;
                            var source = (y * Width + x) * 3;
                            rgb[target] = frames[f][source + 2]; rgb[target + 1] = frames[f][source + 1]; rgb[target + 2] = frames[f][source];
                        }
        return Png(rgb, width, height);
    }

    private static byte[] Png(byte[] rgb, int width, int height)
    {
        using var scanlines = new MemoryStream();
        using (var zlib = new ZLibStream(scanlines, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // No row filter.
                zlib.Write(rgb, y * width * 3, width * 3);
            }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2; // 8 bits per channel, RGB.
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", scanlines.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); png.Write(number);
        png.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); png.Write(number);
    }

    private const string Player = """
        <!doctype html>
        <meta charset="utf-8">
        <title>BUSY Bar preview</title>
        <style>
          body { background: #0b0612; color: #f6eaff; font: 15px system-ui, sans-serif; margin: 24px; }
          h2 { font-size: 15px; font-weight: 500; margin: 20px 0 6px; color: #c0adcd; }
          canvas { display: block; background: #000; border-radius: 6px; max-width: 100%; }
        </style>
        <h1>BUSY Bar preview</h1>
        <p>Each screen plays at the rate stored in its animation file. Play-once screens pause for a second before repeating.</p>
        <div id="screens"></div>
        <script>
        /*SCREENS*/
        const W = 72, H = 16, S = 10;
        for (const screen of screens) {
          const bytes = Uint8Array.from(atob(screen.frames), c => c.charCodeAt(0));
          const count = bytes.length / (W * H * 3);
          const title = document.createElement('h2');
          title.textContent = `${screen.name} · ${count} frames at ${screen.fps} fps · ${(screen.bytes / 1024).toFixed(1)} KB${screen.once ? ' · plays once' : ''}`;
          const canvas = document.createElement('canvas');
          canvas.width = W * S; canvas.height = H * S;
          document.getElementById('screens').append(title, canvas);
          const context = canvas.getContext('2d');
          const start = performance.now();
          const length = count / screen.fps + (screen.once ? 1 : 0);
          (function draw(now) {
            const frame = Math.min(count - 1, Math.floor(((now - start) / 1000 % length) * screen.fps));
            context.fillStyle = '#000'; context.fillRect(0, 0, canvas.width, canvas.height);
            for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
              const p = (frame * W * H + y * W + x) * 3;
              context.fillStyle = `rgb(${bytes[p + 2]},${bytes[p + 1]},${bytes[p]})`;
              context.beginPath(); context.arc(x * S + S / 2, y * S + S / 2, S * 0.42, 0, 7); context.fill();
            }
            requestAnimationFrame(draw);
          })(start);
        }
        </script>
        """;
}
