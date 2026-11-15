using System.Numerics;

namespace BobFx.Core.Services;

public static class BoardSceneRenderer
{
    private const double StripInset = BoardLightShow.StripInset;
    private const double CalibrationLapSeconds = 2;
    internal const string MappingChanged = "LED count changed; update this board's strip mapping.";

    public static List<BoardSettings> AssignedBoards(InstallationSettings settings) => settings.Boards
        .Where(b => b.DeviceMac is not null && settings.Devices.Any(d => d.Mac == b.DeviceMac && d.Selected)).ToList();

    /// <summary>The left strip blue and the right strip amber, each with a white dot running from the near end
    /// to the far end, so an operator can see which strip is which and which way it runs.</summary>
    public static byte[] Calibration(BoardSettings board, int count, double elapsed)
    {
        var output = new byte[count * 3];
        (byte R, byte G, byte B)[] colors = [(0, 96, 160), (160, 96, 0)];
        for (var side = 0; side < 2; side++)
        {
            var color = colors[side];
            var pixels = BoardMapping.StripPixels(board, side);
            foreach (var pixel in pixels)
            {
                if (pixel < 0 || pixel >= count) throw new ArgumentException(MappingChanged);
                output[pixel * 3] = color.R; output[pixel * 3 + 1] = color.G; output[pixel * 3 + 2] = color.B;
            }
            if (pixels.Length == 0) continue;
            var marker = pixels[(int)(elapsed % CalibrationLapSeconds / CalibrationLapSeconds * pixels.Length)];
            output[marker * 3] = output[marker * 3 + 1] = output[marker * 3 + 2] = 220;
        }
        return output;
    }

    /// <summary>A board's LEDs as one loop: up the left strip from the near end, then back down the right.</summary>
    private static int[] Loop(BoardSettings board) =>
        [.. BoardMapping.StripPixels(board, BoardMapping.Left), .. BoardMapping.StripPixels(board, BoardMapping.Right).Reverse()];

    // Traverse each neighboring board in the opposite direction. Begin/end in the middle of the
    // strips that face a neighbor, so the single dot crosses between board loops.
    public static List<(string BoardId, int Pixel)> DotPath(IReadOnlyList<BoardSettings> boards)
    {
        var path = new List<(string, int)>();
        var assigned = boards.Where(b => b.DeviceMac is not null).ToList();
        for (var i = 0; i < assigned.Count; i++)
        {
            var b = assigned[i];
            var loop = Loop(b);
            var join = BoardMapping.StripPixels(b, i == 0 ? BoardMapping.Right : BoardMapping.Left);
            if (join.Length == 0) continue;
            var origin = Array.IndexOf(loop, join[join.Length / 2]);
            for (var p = 0; p < loop.Length; p++)
                path.Add((b.Id, loop[(origin + (i % 2 == 0 ? p : -p) + loop.Length) % loop.Length]));
            // An interior board is entered on its left and must leave on its right.
            // Continue along its loop after the full lap to the handoff point.
            if (i > 0 && i < assigned.Count - 1)
            {
                var exit = BoardMapping.StripPixels(b, BoardMapping.Right);
                if (exit.Length == 0) continue;
                var destination = Array.IndexOf(loop, exit[exit.Length / 2]);
                for (var p = 0; p < loop.Length; p++)
                {
                    var current = (origin + (i % 2 == 0 ? p : -p) + loop.Length) % loop.Length;
                    path.Add((b.Id, loop[current]));
                    if (current == destination) break;
                }
            }
        }
        return path;
    }

    public static byte[] Render(InstallationSettings settings, BoardSettings board, int count, GamePhase phase, double elapsed, double preparationSeconds)
    {
        var output = new byte[count * 3];
        var boards = AssignedBoards(settings);
        var index = boards.FindIndex(b => b.Id == board.Id);
        if (index < 0) return output;
        if (phase == GamePhase.Idle) return output;
        var show = settings.LightShow;
        var timing = settings.Animation;
        var colors = LightPalette.For(show);
        var gain = show.Brightness / 100d;
        byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * gain), 0, 255);
        void Set(int pixel, Rgb color)
        {
            if (pixel < 0 || pixel >= count) return;
            output[pixel * 3] = Channel(color.R); output[pixel * 3 + 1] = Channel(color.G); output[pixel * 3 + 2] = Channel(color.B);
        }
        // The second part of the ending: a comet runs the dot path over a glow that sinks to black.
        Dictionary<int, double>? trail = null;
        Rgb glow = default;
        double since = 0, tail = 1;
        if (phase == GamePhase.Ending && elapsed >= timing.FlashSeconds)
        {
            since = elapsed - timing.FlashSeconds;
            glow = BoardLightShow.Afterglow(colors, since, timing.DotSeconds);
            (var head, tail) = BoardLightShow.Comet(since, timing);
            var path = DotPath(boards);
            trail = [];
            for (var step = 0; step < path.Count; step++)
            {
                if (path[step].BoardId != board.Id) continue;
                // An interior board is on the path twice; each pixel keeps its position nearest the head.
                var behind = BusyBarScene.Frac(head - step / (double)path.Count);
                if (behind < tail && (!trail.TryGetValue(path[step].Pixel, out var nearest) || behind < nearest)) trail[path[step].Pixel] = behind;
            }
        }
        for (var side = 0; side < 2; side++)
        {
            var pixels = BoardMapping.StripPixels(board, side);
            for (var p = 0; p < pixels.Length; p++)
            {
                var along = (p + 0.5) / pixels.Length;
                var x = index + (side == BoardMapping.Left ? StripInset : 1 - StripInset);
                var at = new BoardLightShow.Spot(x, boards.Count, side, index, along, side == BoardMapping.Left ? along / 2 : 1 - along / 2);
                Set(pixels[p], phase switch
                {
                    GamePhase.PreCountdown => BoardLightShow.Countdown(colors, at, elapsed, preparationSeconds),
                    GamePhase.Running => BoardLightShow.Running(colors, show, at, elapsed),
                    _ when trail is null => BoardLightShow.Flare(colors, at, elapsed, timing.FlashSeconds),
                    _ => trail.TryGetValue(pixels[p], out var behind)
                        ? BoardLightShow.CometPixel(colors, glow, behind / tail, since, timing.DotSeconds) : glow
                });
            }
        }
        return output;
    }

    public static byte[] Resample(Vector3[] source, int count)
    {
        var result = new byte[count * 3];
        for (var i = 0; i < count && source.Length > 0; i++)
        {
            var color = Effects.ColorHelper.ToRgbBytes(source[(int)((long)i * source.Length / count)]);
            result[i * 3] = color.r; result[i * 3 + 1] = color.g; result[i * 3 + 2] = color.b;
        }
        return result;
    }
}
