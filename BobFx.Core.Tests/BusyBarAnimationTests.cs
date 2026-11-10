using BobFx.Core.Services;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class BusyBarAnimationTests
{
    private static readonly BusyBarScreen[] Looping = [BusyBarScreen.Idle, BusyBarScreen.Running];

    /// <summary>The frames an asset must contain, rendered independently of the encoder.</summary>
    internal static byte[][] Frames(BusyBarScreen screen, AnimationSettings timing, BusyBarAnimFormat format, int preparation = 5)
    {
        var fps = BusyBarAnimations.Fps(screen, format);
        var count = (int)Math.Round(BusyBarAnimations.Seconds(screen, timing, preparation) * fps);
        return Enumerable.Range(0, count).Select(i => BusyBarAnimations.Render(screen, (double)i / fps, timing, preparation)).ToArray();
    }

    [Fact]
    public void ApiVersionSelectsTheOnlyFormatThatFirmwareCanLoad()
    {
        Assert.Equal(BusyBarAnimFormat.Bicycle0, BusyBarAnimFile.FormatFor("27.5.0"));
        Assert.Equal(BusyBarAnimFormat.Bicycle1, BusyBarAnimFile.FormatFor("27.14.0"));
        Assert.Equal(BusyBarAnimFormat.Bicycle1, BusyBarAnimFile.FormatFor("27.15.0"));
        foreach (var unsupported in new[] { null, "", "nonsense", "27.4.0", "27.9.0", "26.14.0", "28.0.0" })
            Assert.Null(BusyBarAnimFile.FormatFor(unsupported));
    }

    [Fact]
    public void NativeAssetsDecodeUsingFirmware123Layout()
    {
        var assets = BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle0, 5);
        Assert.Equal(5, assets.Count);
        foreach (var (screen, asset) in assets)
        {
            var expected = Frames(screen, new(), BusyBarAnimFormat.Bicycle0);
            Assert.InRange(asset.Data.Length, 58, 255 * 1024);
            using var reader = new BinaryReader(new MemoryStream(asset.Data));
            Assert.Equal("bicycle0", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            Assert.Equal(0, reader.ReadByte()); Assert.Equal(72, reader.ReadByte()); Assert.Equal(16, reader.ReadByte());
            Assert.Equal(0, reader.ReadByte()); // BGR888
            Assert.InRange(reader.ReadByte(), 1, 255);
            var maxEncodedLength = reader.ReadUInt16(); Assert.Equal(0, reader.ReadByte());
            var sectionsLength = reader.ReadUInt32(); var framesLength = reader.ReadUInt32();
            Assert.Equal((uint)asset.Data.Length, 36 + sectionsLength + framesLength);
            Assert.Equal(1u, reader.ReadUInt32());
            var fileFrames = reader.ReadUInt32(); Assert.Equal(fileFrames, reader.ReadUInt32());
            Assert.Equal((uint)expected.Length, fileFrames);
            Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(fileFrames - 1, reader.ReadUInt32());
            Assert.Equal(36 + sectionsLength, reader.ReadUInt32()); Assert.Equal(1, reader.ReadByte());
            Assert.Equal("default\0", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            for (var i = 0; i < fileFrames; i++)
            {
                var encoding = reader.ReadByte(); Assert.InRange(encoding, (byte)0, (byte)1);
                Assert.Equal(1, reader.ReadByte());
                var length = reader.ReadUInt16(); Assert.InRange(length, 1, maxEncodedLength);
                var bytes = reader.ReadBytes(length);
                Assert.Equal(expected[i], encoding == 0 ? bytes : DecodeRle(bytes));
            }
            Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        }
    }

    [Fact]
    public void NativeAssetsDecodeUsingFirmware131LayoutWithinTheSizeBudget()
    {
        var assets = BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle1, 5);
        Assert.Equal(5, assets.Count);
        Assert.InRange(assets.Values.Sum(a => a.Data.Length), 1, 500 * 1024);
        // The longest preparation the game allows still fits one upload.
        Assert.InRange(BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle1, 60)[BusyBarScreen.Countdown].Data.Length, 1, 1024 * 1024);
        foreach (var (screen, asset) in assets)
        {
            var expected = Frames(screen, new(), BusyBarAnimFormat.Bicycle1);
            Assert.InRange(asset.Data.Length, 59, 255 * 1024);
            using var reader = new BinaryReader(new MemoryStream(asset.Data));
            Assert.Equal("bicycle1", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            Assert.Equal(0, reader.ReadByte()); Assert.Equal(72, reader.ReadByte()); Assert.Equal(16, reader.ReadByte());
            Assert.Equal(0, reader.ReadByte()); // BGR888
            var maxMaskBits = reader.ReadUInt16(); var maxPixelBytes = reader.ReadUInt16();
            Assert.InRange(reader.ReadByte(), 1, 255); Assert.Equal(0, reader.ReadByte());
            var sectionsLength = reader.ReadUInt32(); var framesLength = reader.ReadUInt32();
            Assert.Equal((uint)asset.Data.Length, 34 + sectionsLength + framesLength); // The loader rejects any other size.
            var sectionCount = reader.ReadUInt32();
            var fileFrames = reader.ReadUInt32(); Assert.Equal((uint)expected.Length, fileFrames);
            // Section 0 must be "default", span every frame and point at the first frame record. Only the
            // countdown has more: "fromN" starts N seconds in, so a late command can join at the right number.
            var starts = new Dictionary<uint, uint>();
            for (var section = 0; section < sectionCount; section++)
            {
                var start = reader.ReadUInt32(); Assert.Equal(fileFrames - 1, reader.ReadUInt32());
                starts[start] = reader.ReadUInt32();
                var name = new StringBuilder();
                for (var c = reader.ReadByte(); c != 0; c = reader.ReadByte()) name.Append((char)c);
                Assert.Equal(section == 0 ? "default" : $"from{section}", name.ToString());
                Assert.Equal((uint)(section * 20), start);
            }
            Assert.Equal(screen == BusyBarScreen.Countdown ? 5u : 1u, sectionCount);
            Assert.Equal(0, asset.Data[34 + sectionsLength - 1]); // The loader requires a NUL-terminated chunk.
            Assert.Equal(34 + sectionsLength, (uint)reader.BaseStream.Position);
            Assert.Equal(34 + sectionsLength, starts[0]);
            var canvas = new byte[3456];
            int seenMaskBits = 0, seenPixelBytes = 0;
            for (var i = 0; i < fileFrames; i++)
            {
                if (starts.TryGetValue((uint)i, out var offset))
                {
                    Assert.Equal(offset, (uint)reader.BaseStream.Position);
                    Array.Clear(canvas); // Joining at a section must not depend on earlier frames.
                }
                var joint = reader.ReadByte(); var maskBits = reader.ReadUInt16(); var pixelBytes = reader.ReadUInt16();
                var mask = DecodeMask(joint >> 4, reader.ReadBytes((maskBits + 7) / 8), maskBits);
                var data = reader.ReadBytes(pixelBytes);
                Assert.InRange(joint & 15, 0, 1); // Raw or RLE; the QOI-like mode is not used.
                var pixels = (joint & 15) == 0 ? data : DecodeRle(data);
                Assert.Equal(mask.Count(present => present) * 3, pixels.Length); // A mismatch is a frame error on the bar.
                if (starts.ContainsKey((uint)i)) Assert.DoesNotContain(false, mask); // Playback can start here on a stale canvas.
                for (int p = 0, next = 0; p < mask.Length; p++)
                    if (mask[p]) { pixels.AsSpan(next, 3).CopyTo(canvas.AsSpan(p * 3)); next += 3; }
                Assert.Equal(expected[i], canvas);
                seenMaskBits = Math.Max(seenMaskBits, maskBits); seenPixelBytes = Math.Max(seenPixelBytes, pixelBytes);
            }
            Assert.Equal(maxMaskBits, seenMaskBits); Assert.Equal(maxPixelBytes, seenPixelBytes);
            Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        }
    }

    [Fact]
    public void StableAssetNamesChangeOnlyWithRelevantContentAndFormat()
    {
        var original = BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle1, 5);
        var legacy = BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle0, 5);
        var changed = BusyBarAnimations.Create(new() { FlashSeconds = 4 }, BusyBarAnimFormat.Bicycle1, 5);
        var longer = BusyBarAnimations.Create(new(), BusyBarAnimFormat.Bicycle1, 8);
        foreach (var screen in original.Keys)
        {
            Assert.NotEqual(original[screen].Name, legacy[screen].Name);
            Assert.InRange(original[screen].Name.Length, 1, 64);
            // Only the countdown depends on the preparation length.
            Assert.Equal(screen != BusyBarScreen.Countdown, original[screen].Name == longer[screen].Name);
        }
        // Deterministic: the name is a hash of this content.
        Assert.Equal(original[BusyBarScreen.Idle].Data, BusyBarAnimFile.Encode(Frames(BusyBarScreen.Idle, new(), BusyBarAnimFormat.Bicycle1), 20, BusyBarAnimFormat.Bicycle1));
        Assert.StartsWith("bobfx-countdown5-b1-", original[BusyBarScreen.Countdown].Name);
        Assert.StartsWith("bobfx-countdown8-b1-", longer[BusyBarScreen.Countdown].Name);
        Assert.Equal(original[BusyBarScreen.Idle].Name, changed[BusyBarScreen.Idle].Name);
        Assert.NotEqual(original[BusyBarScreen.Ending].Name, changed[BusyBarScreen.Ending].Name);
        static JsonElement Scene(GamePhase phase, double elapsed, AnimationSettings? animation = null, BusyBarAnimFormat format = BusyBarAnimFormat.Bicycle1)
        {
            // Everything on the display is one animation element: no text, no per-frame drawing.
            var elements = JsonSerializer.SerializeToElement(BusyBarService.BuildDisplay(phase, elapsed, 5, animation ?? new(), format)).GetProperty("elements");
            Assert.Equal("animation", Assert.Single(elements.EnumerateArray()).GetProperty("type").GetString());
            return elements[0];
        }
        var running = Scene(GamePhase.Running, 5);
        Assert.True(running.GetProperty("loop").GetBoolean());
        Assert.True(running.GetProperty("await_previous_end").GetBoolean());
        Assert.Equal(15, running.GetProperty("timeout").GetInt32());
        Assert.Equal("default", running.GetProperty("section").GetString());
        // The whole ending is one play-once clip: the flash time plus the dot time, and a second of lease.
        foreach (var elapsed in new[] { 0d, 2.5, 6 })
        {
            var ending = Scene(GamePhase.Ending, elapsed, new() { FlashSeconds = 4 });
            Assert.Contains("bobfx-ending-", ending.GetProperty("path").GetString());
            Assert.False(ending.GetProperty("loop").GetBoolean());
            Assert.False(ending.GetProperty("await_previous_end").GetBoolean());
            Assert.Equal(8, ending.GetProperty("timeout").GetInt32());
        }
        var go = Scene(GamePhase.Running, 0.2);
        Assert.False(go.GetProperty("loop").GetBoolean());
        Assert.Equal(3, go.GetProperty("timeout").GetInt32());
        // The countdown is one play-once clip for the whole preparation, joined part-way when sent late.
        var countdown = Scene(GamePhase.PreCountdown, 0.05);
        Assert.False(countdown.GetProperty("loop").GetBoolean());
        Assert.False(countdown.GetProperty("await_previous_end").GetBoolean());
        Assert.Equal(6, countdown.GetProperty("timeout").GetInt32());
        Assert.Equal("default", countdown.GetProperty("section").GetString());
        Assert.Equal("from3", Scene(GamePhase.PreCountdown, 3.7).GetProperty("section").GetString());
        Assert.Equal("from4", Scene(GamePhase.PreCountdown, 9).GetProperty("section").GetString());
        Assert.Equal("default", Scene(GamePhase.PreCountdown, 3.7, format: BusyBarAnimFormat.Bicycle0).GetProperty("section").GetString());
    }

    [Fact]
    public void EveryScreenStaysInTheSunsetPalette()
    {
        foreach (var screen in Enum.GetValues<BusyBarScreen>())
            foreach (var frame in Frames(screen, new(), BusyBarAnimFormat.Bicycle1))
                for (var p = 0; p < 72 * 16; p++)
                {
                    int b = frame[p * 3], g = frame[p * 3 + 1], r = frame[p * 3 + 2];
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (max < 40 || (max - min) * 2 < max) continue; // Dim or washed-out pixels carry no hue.
                    // Pure green is the one hue the sunset never uses; mint sits at 170 degrees.
                    Assert.False(g == max && g - Math.Max(r, b) > (max - min) / 2, $"{screen} has a green pixel ({r},{g},{b}).");
                }
        foreach (var screen in new[] { BusyBarScreen.Idle, BusyBarScreen.Running })
        {
            var frame = BusyBarAnimations.Render(screen, 4.5, new());
            var lit = Enumerable.Range(0, 72 * 16).Count(p => frame.AsSpan(p * 3, 3).ContainsAnyExcept((byte)0));
            Assert.True(lit > 72 * 16 * 0.9, $"{screen} should fill the display.");
            Assert.Contains(frame, value => value > 200); // The logo is up at full brightness.
        }
    }

    [Fact]
    public void CountdownBakesBoldCentredSecondsAndTheStageBorder()
    {
        // Bright pixels strictly inside the border are the number.
        static (int Count, int Left, int Right, int Top, int Bottom) Digits(byte[] frame)
        {
            var lit = (from y in Enumerable.Range(1, 14) from x in Enumerable.Range(1, 70)
                       where frame.AsSpan((y * 72 + x) * 3, 3).ToArray().Max() > 150 select (x, y)).ToArray();
            return lit.Length == 0 ? default : (lit.Length, lit.Min(p => p.x), lit.Max(p => p.x), lit.Min(p => p.y), lit.Max(p => p.y));
        }
        var seen = new List<int>();
        foreach (var preparation in new[] { 5, 12 })
            for (var second = 0; second < preparation; second++)
            {
                var settled = Digits(BusyBarAnimations.Render(BusyBarScreen.Countdown, second + 0.5, new(), preparation));
                // Centred on the display; digits such as 1 and 5 are not symmetric, so allow their own offset.
                Assert.InRange(settled.Left + settled.Right, 71 - 3, 71 + 3);
                Assert.InRange(settled.Bottom - settled.Top + 1, 12, 13); // Thirteen rows tall of sixteen.
                Assert.InRange(settled.Count, 45, 260); // Thick strokes, not a hairline font.
                Assert.Equal(preparation - second >= 10, settled.Right - settled.Left > 12); // Two digits are wider.
                seen.Add(settled.Count);
                // Each second pops in larger and settles; just before the next it has all but faded out.
                var pop = Digits(BusyBarAnimations.Render(BusyBarScreen.Countdown, second + 0.05, new(), preparation));
                Assert.True(pop.Bottom - pop.Top > settled.Bottom - settled.Top, "The number should start oversized.");
                Assert.Equal(0, Digits(BusyBarAnimations.Render(BusyBarScreen.Countdown, second + 0.99, new(), preparation)).Count);
            }
        Assert.True(seen.Take(5).Distinct().Count() >= 4, "5, 4, 3, 2 and 1 are different shapes.");
        // Border stages as on the boards: scanner, then both long edges, then all four edges.
        static bool Bright(byte[] frame, int x, int y) => frame.AsSpan((y * 72 + x) * 3, 3).ToArray().Max() > 100;
        var scanner = BusyBarAnimations.Render(BusyBarScreen.Countdown, 1.25, new());
        Assert.InRange(Enumerable.Range(0, 72).Count(x => Bright(scanner, x, 0)), 4, 16);
        var players = BusyBarAnimations.Render(BusyBarScreen.Countdown, 3.5, new());
        Assert.All(Enumerable.Range(0, 72), x => Assert.True(Bright(players, x, 0) && Bright(players, x, 15)));
        Assert.False(Bright(players, 0, 8)); Assert.False(Bright(players, 71, 8));
        var all = BusyBarAnimations.Render(BusyBarScreen.Countdown, 4.5, new());
        Assert.All(Enumerable.Range(0, 16), y => Assert.True(Bright(all, 0, y) && Bright(all, 71, y)));
        Assert.All(Enumerable.Range(0, 72), x => Assert.True(Bright(all, x, 0) && Bright(all, x, 15)));
    }

    [Fact]
    public void LoopsCloseWithoutASeam()
    {
        static int Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Sum();
        foreach (var screen in Looping)
        {
            var frames = Frames(screen, new(), BusyBarAnimFormat.Bicycle1);
            var steps = Enumerable.Range(1, frames.Length - 1).Select(i => Difference(frames[i - 1], frames[i])).ToArray();
            // The step from the last frame back to the first must look like the steps on either side of it.
            var wrap = Difference(frames[^1], frames[0]);
            var neighbours = Math.Max(steps[0], steps[^1]);
            Assert.True(wrap <= neighbours * 1.25, $"{screen} jumps when it loops: {wrap} against neighbouring steps of {neighbours}.");
            Assert.True(steps.Min() > 0, $"{screen} should keep moving.");
        }
    }

    [Fact]
    public void ScreensFollowPreparationGoAndEndingBoundaries()
    {
        var a = new AnimationSettings();
        Assert.Equal(BusyBarScreen.Countdown, BusyBarAnimations.Screen(GamePhase.PreCountdown, 4, a));
        Assert.Equal(BusyBarScreen.Go, BusyBarAnimations.Screen(GamePhase.Running, 0, a));
        Assert.Equal(BusyBarScreen.Running, BusyBarAnimations.Screen(GamePhase.Running, 0.75, a));
        Assert.Equal(BusyBarScreen.Ending, BusyBarAnimations.Screen(GamePhase.Ending, 1.9, a));
        Assert.Equal(BusyBarScreen.Ending, BusyBarAnimations.Screen(GamePhase.Ending, 4, a));
        // The burst is over before the running screen takes over, so a late command shows a still frame.
        Assert.NotEqual(BusyBarAnimations.Render(BusyBarScreen.Go, 0, a), BusyBarAnimations.Render(BusyBarScreen.Go, 0.2, a));
        Assert.Equal(BusyBarAnimations.Render(BusyBarScreen.Go, 0.7, a), BusyBarAnimations.Render(BusyBarScreen.Go, 1.4, a));
    }

    [Fact]
    public void EndingPopsGameOverWithThreeFadingFlaresThenItFallsApartAsTheSunSets()
    {
        var a = new AnimationSettings(); // Two seconds of flashes, three of dot, as on the boards.
        var mask = BusyBarScene.GameOver;
        static int Light(byte[] frame) => frame.Sum(value => (int)value);
        static int Brightest(byte[] frame, int x, int y) => frame.AsSpan((y * 72 + x) * 3, 3).ToArray().Max();
        // How many of the lettering's own pixels are lit, and how brightly on average.
        (int Count, double Level) Lettering(double elapsed)
        {
            var frame = BusyBarAnimations.Render(BusyBarScreen.Ending, elapsed, a);
            var levels = (from y in Enumerable.Range(0, 16) from x in Enumerable.Range(0, 72) where mask[x, y] select Brightest(frame, x, y)).ToArray();
            return (levels.Count(level => level > 90), levels.Average());
        }
        var total = Lettering(1).Count;
        Assert.InRange(total, 250, 420); // GAME OVER, whole, during the flares.
        // It is there from the very first frame, oversized and white-hot, then settles.
        var first = BusyBarAnimations.Render(BusyBarScreen.Ending, 0, a);
        Assert.True(Enumerable.Range(0, 72).Count(x => Brightest(first, x, 2) > 200 || Brightest(first, x, 13) > 200) > 20, "The lettering should start larger than its resting ten rows.");
        Assert.Equal(total, Lettering(0.3).Count);
        // Three flares in step with the boards: each lit interval starts bright and has faded by the dark one,
        // the scene never goes black, and the lettering's pulse is weaker each time.
        var pulses = new List<double>();
        for (var flash = 0; flash < 3; flash++)
        {
            var start = Light(BusyBarAnimations.Render(BusyBarScreen.Ending, flash * 2 / 3d + 0.05, a));
            var between = Light(BusyBarAnimations.Render(BusyBarScreen.Ending, flash * 2 / 3d + 0.5, a));
            Assert.True(start > between * 1.5, $"Flare {flash + 1} should stand out: {start} against {between}.");
            Assert.True(between > 10000, "The scene stays visible between flares.");
            pulses.Add(Lettering(flash * 2 / 3d + 0.05).Level - Lettering(flash * 2 / 3d + 0.5).Level);
        }
        Assert.True(pulses[1] > pulses[2] && pulses[2] > 5, "Each pulse of the lettering is weaker than the one before.");
        // Then it falls apart: fewer and fewer pixels are still in place, debris appears below the lettering,
        // and nothing is left at the end.
        var remaining = new[] { 2.05, 2.8, 3.5, 4.2, 4.95 }.Select(t => Lettering(t).Count).ToArray();
        Assert.Equal(total, remaining[0]);
        Assert.True(remaining[1] < remaining[0] && remaining[2] < remaining[1] && remaining[3] < remaining[2], string.Join(", ", remaining));
        Assert.Equal(0, remaining[4]);
        var falling = BusyBarAnimations.Render(BusyBarScreen.Ending, 3.2, a);
        var still = BusyBarAnimations.Render(BusyBarScreen.Ending, 1.9, a);
        static int Below(byte[] frame) => (from y in Enumerable.Range(13, 3) from x in Enumerable.Range(6, 60) where Brightest(frame, x, y) > 60 select 1).Count();
        Assert.True(Below(falling) > Below(still) + 10, "Pixels should be dropping below the lettering.");
        // The sun has set by the end, so the idle loop's sunrise follows on: nothing sun-yellow is left.
        var last = BusyBarAnimations.Render(BusyBarScreen.Ending, 4.95, a);
        Assert.DoesNotContain(Enumerable.Range(0, 72 * 9), p => last[p * 3 + 2] > 200 && last[p * 3 + 1] > 150);
        var idle = BusyBarAnimations.Render(BusyBarScreen.Idle, 0, a);
        Assert.DoesNotContain(Enumerable.Range(0, 72 * 9), p => idle[p * 3 + 2] > 200 && idle[p * 3 + 1] > 150);
    }

    // Independent decoder of the firmware toolbox RLE (3-byte BGR blocks).
    private static byte[] DecodeRle(byte[] input)
    {
        using var output = new MemoryStream();
        for (var offset = 0; offset < input.Length;)
        {
            var opcode = input[offset++]; var blocks = opcode & 127;
            Assert.InRange(blocks, 1, 127);
            if ((opcode & 128) != 0)
            {
                output.Write(input, offset, blocks * 3); offset += blocks * 3;
            }
            else
            {
                for (var i = 0; i < blocks; i++) output.Write(input, offset, 3);
                offset += 3;
            }
        }
        return output.ToArray();
    }

    // Independent decoder of the bicycle1 change mask: which of the 1152 pixels a frame carries.
    private static bool[] DecodeMask(int encoding, byte[] data, int bits)
    {
        const int pixels = 72 * 16;
        if (encoding is 0 or 1)
        {
            Assert.Equal(0, bits);
            return Enumerable.Repeat(encoding == 1, pixels).ToArray();
        }
        var position = 0;
        int Read(int width)
        {
            var value = 0;
            for (var i = 0; i < width; i++, position++) value = value << 1 | data[position / 8] >> 7 - position % 8 & 1;
            return value;
        }
        if (encoding == 4)
        {
            Assert.Equal(pixels, bits);
            return Enumerable.Range(0, pixels).Select(_ => Read(1) == 1).ToArray();
        }
        Assert.InRange(encoding, 2, 3);
        Assert.InRange(bits, 1, (int)(pixels * 0.75));
        var mask = new List<bool>(pixels);
        for (var present = encoding == 3; position < bits; present = !present)
        {
            var length = Read(3);
            if (length == 7) length = Read(8);
            mask.AddRange(Enumerable.Repeat(present, length));
        }
        Assert.Equal(bits, position);
        Assert.Equal(pixels, mask.Count);
        return [.. mask];
    }
}
