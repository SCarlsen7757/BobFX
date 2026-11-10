using System.Text;

namespace BobFx.Core.Services;

internal enum BusyBarAnimFormat { Bicycle0, Bicycle1 }

/// <summary>Native BUSY Bar animation containers for 72x16 BGR888 frames.
/// bicycle0: firmware 1.2.x (API 27.5), full RLE frames.
/// bicycle1: firmware 1.3+ (API 27.14+), delta frames with a change mask.
/// References: busy-app/busybar-firmware tags 1.2.3 and 1.3.1, lib/anim_file/anim_file_format.h,
/// lib/anim_file/components/anim_file_load.c and scripts/seq2anim.py.</summary>
internal static class BusyBarAnimFile
{
    internal const int Width = 72, Height = 16, PixelCount = Width * Height, FrameBytes = PixelCount * 3;

    /// <summary>The firmware loads exactly one container version and reports success for a
    /// file it cannot play, so the format follows the reviewed API versions only.</summary>
    internal static BusyBarAnimFormat? FormatFor(string? apiVersion) =>
        !Version.TryParse(apiVersion, out var version) || version.Major != 27 ? null
        : version.Minor == 5 ? BusyBarAnimFormat.Bicycle0
        : version.Minor >= 14 ? BusyBarAnimFormat.Bicycle1
        : null;

    /// <param name="sections">Extra named frame ranges, each running from its start frame to the end of the
    /// file, so playback can join part-way through. Only bicycle1 carries them.</param>
    internal static byte[] Encode(IReadOnlyList<byte[]> frames, int fps, BusyBarAnimFormat format, IReadOnlyList<(string Name, int Start)>? sections = null)
    {
        if (frames.Count == 0 || fps is < 1 or > 255 || frames.Any(f => f.Length != FrameBytes))
            throw new ArgumentException("Native animation needs full 72x16 BGR frames and a valid frame rate.");
        return format == BusyBarAnimFormat.Bicycle0 ? EncodeBicycle0(frames, fps) : EncodeBicycle1(frames, fps, sections ?? []);
    }

    private static byte[] EncodeBicycle0(IReadOnlyList<byte[]> frames, int fps)
    {
        var encoded = frames.Select(f => Smaller(f, Rle(f))).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        const uint headerLength = 36, sectionLength = 21; // <8s BBBB BHB II III, then <IIIB + "default\0"
        writer.Write("bicycle0"u8); writer.Write((byte)0); writer.Write((byte)Width); writer.Write((byte)Height); writer.Write((byte)0);
        writer.Write((byte)fps); writer.Write((ushort)encoded.Max(f => f.Length)); writer.Write((byte)0);
        writer.Write(sectionLength); writer.Write((uint)encoded.Sum(f => 4 + f.Length));
        writer.Write(1u); writer.Write((uint)frames.Count); writer.Write((uint)frames.Count);
        writer.Write(0u); writer.Write((uint)(frames.Count - 1)); writer.Write(headerLength + sectionLength); writer.Write((byte)1);
        writer.Write("default\0"u8);
        for (var i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(ReferenceEquals(encoded[i], frames[i]) ? 0 : 1)); writer.Write((byte)1);
            writer.Write((ushort)encoded[i].Length); writer.Write(encoded[i]);
        }
        return stream.ToArray();
    }

    private static byte[] EncodeBicycle1(IReadOnlyList<byte[]> frames, int fps, IReadOnlyList<(string Name, int Start)> sections)
    {
        if (sections.Any(s => s.Start < 0 || s.Start >= frames.Count || s.Name is "default" or "" || s.Name.Any(c => c is < '!' or > '~')))
            throw new ArgumentException("Sections need a printable name other than default and a start frame inside the animation.");
        var records = new List<FrameRecord>(frames.Count);
        var offsets = new List<uint>(frames.Count);
        var sectionsLength = (uint)(20 + sections.Sum(s => 12 + s.Name.Length + 1)); // <III + name + NUL each; "default" first
        const uint headerLength = 34; // <8s BBBB HHBB II II
        var offset = headerLength + sectionsLength;
        for (var i = 0; i < frames.Count; i++)
        {
            // Frame 0 and every section start carry all pixels: playback can begin there, and a
            // loop restarts at frame 0 without clearing the canvas.
            var record = i == 0 || sections.Any(s => s.Start == i) ? FullFrame(frames[i]) : DeltaFrame(frames[i], frames[i - 1]);
            records.Add(record); offsets.Add(offset); offset += (uint)record.Length;
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("bicycle1"u8); writer.Write((byte)0); writer.Write((byte)Width); writer.Write((byte)Height); writer.Write((byte)0);
        writer.Write((ushort)records.Max(r => r.MaskBits)); writer.Write((ushort)records.Max(r => r.Pixels.Length));
        writer.Write((byte)fps); writer.Write((byte)0);
        writer.Write(sectionsLength); writer.Write((uint)records.Sum(r => r.Length));
        writer.Write((uint)(1 + sections.Count)); writer.Write((uint)frames.Count);
        foreach (var (name, start) in sections.Prepend(("default", 0)))
        {
            writer.Write((uint)start); writer.Write((uint)(frames.Count - 1)); writer.Write(offsets[start]);
            writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write((byte)0);
        }
        foreach (var record in records)
        {
            writer.Write((byte)(record.PixelEncoding | record.MaskEncoding << 4));
            writer.Write((ushort)record.MaskBits); writer.Write((ushort)record.Pixels.Length);
            writer.Write(record.Mask); writer.Write(record.Pixels);
        }
        return stream.ToArray();
    }

    // Mask encodings: 0 nothing changed, 1 every pixel present, 2/3 run lengths starting black/white, 4 bitmap.
    private sealed record FrameRecord(int MaskEncoding, byte[] Mask, int MaskBits, int PixelEncoding, byte[] Pixels)
    {
        public int Length => 5 + Mask.Length + Pixels.Length;
    }

    private static FrameRecord FullFrame(byte[] frame)
    {
        var pixels = Smaller(frame, Rle(frame));
        return new(1, [], 0, ReferenceEquals(pixels, frame) ? 0 : 1, pixels);
    }

    private static FrameRecord DeltaFrame(byte[] frame, byte[] previous)
    {
        var changed = new bool[PixelCount];
        using var kept = new MemoryStream();
        for (var p = 0; p < PixelCount; p++)
        {
            if (frame.AsSpan(p * 3, 3).SequenceEqual(previous.AsSpan(p * 3, 3))) continue;
            changed[p] = true;
            kept.Write(frame, p * 3, 3);
        }
        if (kept.Length == 0) return new(0, [], 0, 0, []);
        var full = FullFrame(frame);
        if (kept.Length == FrameBytes) return full;
        var raw = kept.ToArray();
        var pixels = Smaller(raw, Rle(raw));
        var (encoding, mask, bits) = Mask(changed);
        var delta = new FrameRecord(encoding, mask, bits, ReferenceEquals(pixels, raw) ? 0 : 1, pixels);
        return delta.Length < full.Length ? delta : full;
    }

    /// <summary>Same choice as the official converter: binary run lengths when they save at
    /// least a quarter of the bitmap, otherwise one bit per pixel, most significant bit first.</summary>
    private static (int Encoding, byte[] Data, int Bits) Mask(bool[] changed)
    {
        var bits = new BitWriter();
        for (var start = 0; start < changed.Length;)
        {
            var length = 1;
            while (start + length < changed.Length && changed[start + length] == changed[start]) length++;
            start += length;
            if (length < 7) { bits.Write(length, 3); continue; }
            // 3-bit lengths end at 6; 7 escapes to an 8-bit length. Longer runs are split
            // by a zero-length run of the opposite colour.
            while (length >= 255) { bits.Write(7, 3); bits.Write(255, 8); length -= 255; bits.Write(0, 3); }
            bits.Write(7, 3); bits.Write(length, 8);
        }
        if (bits.Count <= changed.Length * 0.75) return (changed[0] ? 3 : 2, bits.ToArray(), bits.Count);
        var bitmap = new byte[(changed.Length + 7) / 8];
        for (var p = 0; p < changed.Length; p++)
            if (changed[p]) bitmap[p / 8] |= (byte)(0x80 >> p % 8);
        return (4, bitmap, changed.Length);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        public int Count { get; private set; }
        public void Write(int value, int width)
        {
            for (var bit = width - 1; bit >= 0; bit--, Count++)
            {
                if (Count % 8 == 0) bytes.Add(0);
                if ((value >> bit & 1) != 0) bytes[^1] |= (byte)(0x80 >> Count % 8);
            }
        }
        public byte[] ToArray() => [.. bytes];
    }

    private static byte[] Smaller(byte[] raw, byte[] encoded) => encoded.Length < raw.Length ? encoded : raw;

    /// <summary>Firmware toolbox RLE over 3-byte BGR blocks: an opcode with bit 7 clear repeats
    /// the next block 1..127 times; with bit 7 set it is followed by 1..127 literal blocks.</summary>
    private static byte[] Rle(byte[] pixels)
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
        return result.ToArray();
    }
}
