using BobFx.Core.Services;
using Xunit;

namespace BobFx.Core.Tests;

/// <summary>Properties of the board light show, sampled at explicit times.</summary>
public sealed class BoardLightShowTests
{
    private const int Leds = 40, Preparation = 3;
    private static readonly string[] Macs = ["001122334455", "aabbccddeeff"];

    /// <summary>Two boards side by side, ten LEDs per edge.</summary>
    private static InstallationSettings Row(LightShowSettings? show = null) => new()
    {
        Devices = Macs.Select(mac => new DeviceSettings(mac, "Board", "192.168.1.20", 80, Leds, 21324)).ToList(),
        Boards = Macs.Select((mac, index) => new BoardSettings { Id = index == 0 ? "a" : "b", Name = "Board", DeviceMac = mac,
            Edges = [new(0, 10), new(10, 10), new(20, 10), new(30, 10)] }).ToList(),
        LightShow = show ?? new()
    };

    /// <summary>Every board's frame in row order.</summary>
    private static byte[] Frame(InstallationSettings settings, GamePhase phase, double elapsed) =>
        settings.Boards.SelectMany(board => BoardSceneRenderer.Render(settings, board, Leds, phase, elapsed, Preparation)).ToArray();

    private static IEnumerable<byte[]> Pixels(byte[] frame) => frame.Chunk(3);

    private static readonly LightShowColors Red = new() { Primary = "#ff0000", Secondary = "#ff0000", Tertiary = "#ff0000", Accent = "#ff0000" };

    public static TheoryData<string, string, int, int> EverySetting()
    {
        var data = new TheoryData<string, string, int, int>();
        foreach (var theme in new[] { "neonSunset", "aurora", "ember", "custom" })
        foreach (var intensity in new[] { "calm", "balanced", "lively" })
        foreach (var speed in new[] { 25, 100, 200 })
        foreach (var brightness in new[] { 20, 100 })
            data.Add(theme, intensity, speed, brightness);
        return data;
    }

    [Theory]
    [MemberData(nameof(EverySetting))]
    public void PlayingIsNeverStaticAtAnySetting(string theme, string intensity, int speed, int brightness)
    {
        // The custom theme here is a single colour, so only the breathing can move it.
        var settings = Row(new() { Theme = theme, Intensity = intensity, Speed = speed, Brightness = brightness, Custom = Red });
        foreach (var time in new[] { 2d, 10, 47.3, 600, 3599 })
            Assert.NotEqual(Frame(settings, GamePhase.Running, time), Frame(settings, GamePhase.Running, time + 3));
    }

    [Fact]
    public void PlayingVisiblyMovesWithinASecondAtTheDefaults()
    {
        var settings = Row();
        foreach (var time in new[] { 5d, 20, 61.5 })
        {
            var before = Frame(settings, GamePhase.Running, time);
            var after = Frame(settings, GamePhase.Running, time + 1);
            var moved = Pixels(before).Zip(Pixels(after)).Count(pair => !pair.First.SequenceEqual(pair.Second));
            Assert.True(moved >= Leds, $"Only {moved} of {Leds * 2} pixels changed in a second at {time}s.");
        }
    }

    [Theory]
    [InlineData("calm", 100, 2)]
    [InlineData("lively", 200, 12)]
    public void PlayingStaysCalmAndCapped(string intensity, int speed, int largestStep)
    {
        var settings = Row(new() { Intensity = intensity, Speed = speed });
        var cap = 255 * (0.35 + 0.20 * (BoardLightShow.Level(intensity) ?? 0)) + 1;
        var previous = Frame(settings, GamePhase.Running, 1.5);
        for (var frame = 1; frame <= 60 * 30; frame++)
        {
            var current = Frame(settings, GamePhase.Running, 1.5 + frame / 30d);
            // No channel jumps between frames, nothing exceeds the play level, and no LED goes dark.
            Assert.All(current.Zip(previous), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, largestStep));
            Assert.All(current, value => Assert.InRange(value, 0, cap));
            Assert.All(Pixels(current), pixel => Assert.True(pixel.Max() > 0));
            previous = current;
        }
    }

    [Fact]
    public void EveryPhaseKeepsMoving()
    {
        var settings = Row();
        var ending = settings.Animation.FlashSeconds + settings.Animation.DotSeconds;
        // The scanner sweeps back and forth and so revisits positions; what matters is that no half
        // second passes without the picture changing.
        foreach (var (phase, until) in new[] { (GamePhase.PreCountdown, 2.5), (GamePhase.Running, 3d), (GamePhase.Ending, ending - 1) })
            for (var time = 0d; time < until; time += 0.05)
            {
                var first = Frame(settings, phase, time);
                Assert.True(!first.SequenceEqual(Frame(settings, phase, time + 0.2)) || !first.SequenceEqual(Frame(settings, phase, time + 0.4)),
                    $"{phase} stood still for 0.4s from {time:0.00}s.");
            }
    }

    [Fact]
    public void BigMomentsOutshinePlay()
    {
        var settings = Row();
        var play = Enumerable.Range(0, 200).Max(step => Frame(settings, GamePhase.Running, 2 + step * 0.37).Max());
        Assert.InRange(play, 60, 91);
        Assert.True(Frame(settings, GamePhase.PreCountdown, 1.02).Max() >= 230);
        Assert.True(Frame(settings, GamePhase.Running, 0.05).Max() >= 230);
        Assert.True(Frame(settings, GamePhase.Ending, 0.02).Max() >= 200);
    }

    [Fact]
    public void ThemeAndBrightnessApply()
    {
        // Neon sunset never shows the old plain green at any moment; its GO is mint, which carries blue.
        var sunset = Row();
        foreach (var (phase, until) in new[] { (GamePhase.PreCountdown, 3d), (GamePhase.Running, 40d), (GamePhase.Ending, 5d) })
            for (var time = 0d; time < until; time += 0.13)
                Assert.All(Pixels(Frame(sunset, phase, time)), pixel => Assert.False(pixel[1] > pixel[0] + 40 && pixel[1] > pixel[2] + 40,
                    $"Green pixel {pixel[0]},{pixel[1]},{pixel[2]} in {phase} at {time:0.00}s."));

        // The themes differ, and a custom theme uses exactly the chosen colours during play.
        Assert.NotEqual(Frame(sunset, GamePhase.Running, 10), Frame(Row(new() { Theme = "aurora" }), GamePhase.Running, 10));
        Assert.NotEqual(Frame(sunset, GamePhase.Running, 10), Frame(Row(new() { Theme = "ember" }), GamePhase.Running, 10));
        var red = Frame(Row(new() { Theme = "custom", Custom = Red, Intensity = "lively" }), GamePhase.Running, 10);
        Assert.All(Pixels(red), pixel => { Assert.True(pixel[0] > 0); Assert.Equal(0, pixel[1]); Assert.Equal(0, pixel[2]); });

        // Brightness scales the output.
        var full = Frame(sunset, GamePhase.Running, 10);
        var half = Frame(Row(new() { Brightness = 50 }), GamePhase.Running, 10);
        Assert.All(full.Zip(half), pair => Assert.InRange(pair.Second - pair.First / 2d, -1, 1));
    }

    [Fact]
    public void CountdownPopsEachSecondAndFollowsBarStages()
    {
        var settings = Row();
        byte Dimmest(double time) => Pixels(Frame(settings, GamePhase.PreCountdown, time)).Min(pixel => pixel.Min());
        // Every LED goes white-hot on each count, harder as GO approaches.
        Assert.True(Dimmest(0.02) >= 130);
        Assert.True(Dimmest(1.02) > Dimmest(0.02));
        Assert.True(Dimmest(2.02) > Dimmest(1.02));

        bool Lit(byte[] frame, int board, int edge) => Enumerable.Range(0, 10).All(p => frame.Skip((board * Leds + edge * 10 + p) * 3).Take(3).Max() > 0);
        bool Dark(byte[] frame, int board, int edge) => Enumerable.Range(0, 10).All(p => frame.Skip((board * Leds + edge * 10 + p) * 3).Take(3).Max() == 0);
        // Scanner: half a second after a count it has reached the far end of the row.
        var scanner = Frame(settings, GamePhase.PreCountdown, 0.5);
        Assert.True(Dark(scanner, 0, 3)); Assert.True(Lit(scanner, 1, 1));
        // From 60 % only the player edges pulse, from 80 % a chase runs round every edge.
        var players = Frame(settings, GamePhase.PreCountdown, 2.35);
        foreach (var board in new[] { 0, 1 })
        {
            Assert.True(Lit(players, board, 0)); Assert.True(Lit(players, board, 2));
            Assert.True(Dark(players, board, 1)); Assert.True(Dark(players, board, 3));
        }
        var chase = Frame(settings, GamePhase.PreCountdown, 2.5);
        Assert.All(Pixels(chase), pixel => Assert.True(pixel.Max() > 0));
    }

    [Fact]
    public void GoBurstRacesOutFromRowCentre()
    {
        var settings = Row();
        // Distance from the row centre of the whitest LED on the first board's top edge.
        double Front(double time)
        {
            var frame = Frame(settings, GamePhase.Running, time);
            var whitest = Enumerable.Range(0, 10).MaxBy(p => frame.Skip(p * 3).Take(3).Min());
            return 1 - (whitest + 0.5) / 10;
        }
        Assert.True(Front(0.05) < Front(0.15));
        Assert.True(Front(0.15) < Front(0.25));
        // Afterwards the burst has passed and play takes over without a flash.
        Assert.True(Frame(settings, GamePhase.Running, 1.6).Max() <= 91);
    }

    [Fact]
    public void GameOverFlaresWeakenThenCometFadesToBlack()
    {
        var settings = Row();
        var timing = settings.Animation;
        int Level(double time) => Frame(settings, GamePhase.Ending, time).Sum(value => value);
        var third = timing.FlashSeconds / 3;
        // Three flares, each weaker, with near darkness between them.
        Assert.True(Level(0.02) > Level(third + 0.02));
        Assert.True(Level(third + 0.02) > Level(third * 2 + 0.02));
        foreach (var flare in new[] { 0, 1, 2 })
            Assert.True(Frame(settings, GamePhase.Ending, third * flare + third * 0.75).Max() < 30);

        // The comet has a tail, slows down and is gone when the ending is over.
        var comet = Frame(settings, GamePhase.Ending, timing.FlashSeconds + 1);
        Assert.True(Pixels(comet).Count(pixel => pixel.Max() > 100) > 1);
        var early = BoardLightShow.Comet(0.5, timing).Head - BoardLightShow.Comet(0.4, timing).Head;
        var late = BoardLightShow.Comet(2.9, timing).Head - BoardLightShow.Comet(2.8, timing).Head;
        Assert.True((late + 1) % 1 < (early + 1) % 1);
        Assert.All(Frame(settings, GamePhase.Ending, timing.FlashSeconds + timing.DotSeconds - 0.001), value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void PreviewFramesMatchWhatTheBoardsReceive()
    {
        var saved = Row();
        var draft = new LightShowSettings { Theme = "ember", Brightness = 60 };
        var preview = LightShowPreviewRenderer.Render(saved, new("countdown", draft, new()));
        Assert.False(preview.ExampleBoards);
        Assert.Equal(20, preview.FramesPerSecond);
        Assert.Equal(6, preview.Seconds);
        Assert.Equal(120, preview.Frames.Count);
        Assert.Equal(["a", "b"], preview.Boards.Select(b => b.Id));
        // Frame 30 is 1.5 s into the countdown: both boards in row order, exactly as the output loop renders them.
        var drafted = saved with { LightShow = draft };
        Assert.Equal(Frame(drafted, GamePhase.PreCountdown, 1.5), Convert.FromBase64String(preview.Frames[30]));
        // The draft is previewed, not the saved theme.
        Assert.NotEqual(Frame(saved, GamePhase.PreCountdown, 1.5), Convert.FromBase64String(preview.Frames[30]));
    }

    [Fact]
    public void PreviewUsesExampleBoardsUntilTheLayoutHasOneAndRejectsBadDrafts()
    {
        var empty = new InstallationSettings();
        var preview = LightShowPreviewRenderer.Render(empty, new("gameOver", new(), new()));
        Assert.True(preview.ExampleBoards);
        Assert.Equal(2, preview.Boards.Count);
        Assert.All(preview.Boards, board => { Assert.Equal(60, board.LedCount); Assert.False(board.Assigned); });
        Assert.Equal(100, preview.Frames.Count);
        Assert.Equal(2 * 60 * 3, Convert.FromBase64String(preview.Frames[0]).Length);
        Assert.Throws<ArgumentException>(() => LightShowPreviewRenderer.Render(empty, new("idle", new(), new())));
        Assert.Throws<ArgumentException>(() => LightShowPreviewRenderer.Render(empty, new("playing", new() { Brightness = 0 }, new())));
        Assert.Throws<ArgumentException>(() => LightShowPreviewRenderer.Render(empty, new("playing", null!, new())));
    }

    [Fact]
    public void PreviewDrawsTheWholeRowIncludingBoardsWithoutAController()
    {
        // One real board in the middle of three slots, as when only one controller is at hand.
        var real = Row();
        var layout = real with { Boards = [new() { Id = "left", Name = "Left" }, real.Boards[0], new() { Id = "right", Name = "Right", Rotation = 90 }] };
        var preview = LightShowPreviewRenderer.Render(layout, new("gameOver", new(), new()));
        Assert.False(preview.ExampleBoards);
        Assert.Equal(["left", "a", "right"], preview.Boards.Select(b => b.Id));
        Assert.Equal([false, true, false], preview.Boards.Select(b => b.Assigned));
        // Boards without a controller copy the real board's LED count and mapping, but keep their own rotation.
        Assert.Equal([Leds, Leds, Leds], preview.Boards.Select(b => b.LedCount));
        Assert.All(preview.Boards, b => Assert.Equal(real.Boards[0].Edges, b.Edges));
        Assert.Equal([0, 0, 90], preview.Boards.Select(b => b.Rotation));
        Assert.Equal(3 * Leds * 3, Convert.FromBase64String(preview.Frames[0]).Length);
        // The comet runs the whole row, so every board is lit at some point after the flares.
        var lit = new bool[3];
        foreach (var frame in preview.Frames.Skip(2 * LightShowPreviewRenderer.FramesPerSecond).Select(Convert.FromBase64String))
        {
            var spans = new[] { (0, Leds), (Leds, Leds), (2 * Leds, Leds) };
            for (var board = 0; board < 3; board++)
                if (frame.Skip(spans[board].Item1 * 3).Take(spans[board].Item2 * 3).Any(value => value > 100)) lit[board] = true;
        }
        Assert.All(lit, Assert.True);
        // The saved settings are not touched: games still skip the empty slots.
        Assert.Single(BoardSceneRenderer.AssignedBoards(layout));
        // With no controller anywhere, slots fall back to 60 LEDs.
        var none = LightShowPreviewRenderer.Render(new InstallationSettings { Boards = [new() { Id = "x", Name = "X" }] }, new("playing", new(), new()));
        Assert.Equal(60, Assert.Single(none.Boards).LedCount);
        Assert.False(none.ExampleBoards);
    }

    [Fact]
    public void TestSceneMapsSegmentsToPhasesAndEnds()
    {
        var timing = new AnimationSettings();
        Assert.Equal((GamePhase.PreCountdown, 0d), BoardLightShow.TestScene("countdown", 0, timing));
        Assert.Equal(GamePhase.PreCountdown, BoardLightShow.TestScene("countdown", 2.9, timing)!.Value.Phase);
        Assert.Equal((GamePhase.Running, 0.5), BoardLightShow.TestScene("countdown", 3.5, timing));
        Assert.Null(BoardLightShow.TestScene("countdown", 6.1, timing));
        // The playing test starts after GO and its blend, so it shows play only.
        Assert.Equal((GamePhase.Running, 1.5), BoardLightShow.TestScene("playing", 0, timing));
        Assert.Null(BoardLightShow.TestScene("playing", 30.1, timing));
        Assert.Equal((GamePhase.Ending, 1d), BoardLightShow.TestScene("gameOver", 1, timing));
        Assert.Null(BoardLightShow.TestScene("gameOver", timing.FlashSeconds + timing.DotSeconds + 0.1, timing));
        Assert.Null(BoardLightShow.TestScene("idle", 0, timing));
        Assert.Equal(6, BoardLightShow.TestSeconds("countdown", timing));
        Assert.Equal(30, BoardLightShow.TestSeconds("playing", timing));
        Assert.Equal(5, BoardLightShow.TestSeconds("gameOver", timing));
        Assert.Null(BoardLightShow.TestSeconds("idle", timing));
    }
}
