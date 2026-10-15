using BobFx.Core.Services;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class BusyBarAnimationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAssetsDecodeUsingFirmware123Layout(bool showTimer)
    {
        var assets = BusyBarAnimations.Create(showTimer, new());
        Assert.Equal(8, assets.Count);
        foreach (var (screen, asset) in assets)
        {
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
            Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(fileFrames - 1, reader.ReadUInt32());
            Assert.Equal(36 + sectionsLength, reader.ReadUInt32()); Assert.Equal(1, reader.ReadByte());
            Assert.Equal("default\0", Encoding.ASCII.GetString(reader.ReadBytes(8)));
            for (var i = 0; i < fileFrames; i++)
            {
                var encoding = reader.ReadByte(); Assert.InRange(encoding, (byte)0, (byte)1);
                Assert.Equal(1, reader.ReadByte());
                var length = reader.ReadUInt16(); Assert.InRange(length, 1, maxEncodedLength);
                var bytes = reader.ReadBytes(length);
                var decoded = encoding == 0 ? bytes : DecodeRle(bytes);
                Assert.Equal(3456, decoded.Length);
                if (i == 0) Assert.Equal(BusyBarAnimations.Render(screen, showTimer, 0, new()), decoded);
            }
            Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        }
    }

    [Fact]
    public void StableAssetNamesChangeOnlyWithRelevantContentAndNeverIncludeGameTime()
    {
        var original = BusyBarAnimations.Create(false, new());
        var repeated = BusyBarAnimations.Create(false, new());
        var timer = BusyBarAnimations.Create(true, new());
        var changed = BusyBarAnimations.Create(false, new() { FlashSeconds = 4, DotLapsPerSecond = 2 });
        foreach (var screen in original.Keys) Assert.Equal(original[screen].Name, repeated[screen].Name);
        Assert.Equal(original[BusyBarScreen.Idle].Name, timer[BusyBarScreen.Idle].Name);
        Assert.NotEqual(original[BusyBarScreen.Running].Name, timer[BusyBarScreen.Running].Name);
        Assert.NotEqual(original[BusyBarScreen.Flash].Name, changed[BusyBarScreen.Flash].Name);
        Assert.NotEqual(original[BusyBarScreen.Dot].Name, changed[BusyBarScreen.Dot].Name);
        var one = JsonSerializer.Serialize(BusyBarService.BuildDisplay(false, GamePhase.Running, 5, 5, TimeSpan.FromSeconds(123), new()));
        var two = JsonSerializer.Serialize(BusyBarService.BuildDisplay(false, GamePhase.Running, 5, 5, TimeSpan.FromSeconds(456), new()));
        Assert.Equal(one, two);
        var display = JsonSerializer.Deserialize<JsonElement>(one).GetProperty("elements");
        Assert.Equal(1, display.GetArrayLength());
        Assert.True(display[0].GetProperty("await_previous_end").GetBoolean());
        Assert.Equal(3, display[0].GetProperty("timeout").GetInt32());
        var flash = JsonSerializer.SerializeToElement(BusyBarService.BuildDisplay(false, GamePhase.Ending, 0, 5, TimeSpan.Zero, new() { FlashSeconds = 4 })).GetProperty("elements")[0];
        Assert.False(flash.GetProperty("loop").GetBoolean());
        Assert.False(flash.GetProperty("await_previous_end").GetBoolean());
        Assert.Equal(5, flash.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public void ScreensFollowPreparationGoAndEndingBoundaries()
    {
        var a = new AnimationSettings();
        Assert.Equal(BusyBarScreen.Scanner, BusyBarAnimations.Screen(GamePhase.PreCountdown, 0, 5, a));
        Assert.Equal(BusyBarScreen.PlayerEdges, BusyBarAnimations.Screen(GamePhase.PreCountdown, 3, 5, a));
        Assert.Equal(BusyBarScreen.AllEdges, BusyBarAnimations.Screen(GamePhase.PreCountdown, 4, 5, a));
        Assert.Equal(BusyBarScreen.Go, BusyBarAnimations.Screen(GamePhase.Running, 0, 0, a));
        Assert.Equal(BusyBarScreen.Running, BusyBarAnimations.Screen(GamePhase.Running, 0.75, 0, a));
        Assert.Equal(BusyBarScreen.Flash, BusyBarAnimations.Screen(GamePhase.Ending, 1.9, 0, a));
        Assert.Equal(BusyBarScreen.Dot, BusyBarAnimations.Screen(GamePhase.Ending, 2, 0, a));
        // Three visible red intervals, each followed by a black interval.
        for (var interval = 0; interval < 6; interval++)
        {
            var pixels = BusyBarAnimations.Render(BusyBarScreen.Flash, false, (interval + 0.5) / 3, a);
            Assert.Equal(interval % 2 == 0, pixels.Any(b => b != 0));
        }
        var dot = BusyBarAnimations.Render(BusyBarScreen.Dot, false, 0.13, a);
        Assert.Equal(1, dot.Count(b => b != 0));
    }

    // Independent decoder of firmware 1.2.3's toolbox RLE (3-byte BGR blocks).
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
}
