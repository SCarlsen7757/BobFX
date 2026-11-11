using System.Numerics;

namespace BobFx.Core.Services;

public static class BoardSceneRenderer
{
    public static List<BoardSettings> AssignedBoards(InstallationSettings settings) => settings.Boards
        .Where(b => b.DeviceMac is not null && settings.Devices.Any(d => d.Mac == b.DeviceMac && d.Selected)).ToList();

    public static byte[] Calibration(BoardSettings board, int count, double elapsed)
    {
        var output = new byte[count * 3];
        (byte R, byte G, byte B)[] colors = [(0, 96, 160), (0, 128, 64), (160, 96, 0), (144, 0, 96)];
        var perimeter = new List<int>();
        for (var side = 0; side < 4; side++)
        {
            var color = colors[side];
            foreach (var pixel in EdgePixels(board, side))
            {
                if (pixel < 0 || pixel >= count) throw new ArgumentException("LED count changed; update this board's edge mapping.");
                output[pixel * 3] = color.R; output[pixel * 3 + 1] = color.G; output[pixel * 3 + 2] = color.B;
                perimeter.Add(pixel);
            }
        }
        if (perimeter.Count > 0)
        {
            var marker = perimeter[(int)(elapsed % 4 / 4 * perimeter.Count)];
            output[marker * 3] = output[marker * 3 + 1] = output[marker * 3 + 2] = 220;
        }
        return output;
    }
    public static int[] EdgePixels(BoardSettings board, int visualEdge)
    {
        var edge = board.Edges[(visualEdge - board.Rotation / 90 + 4) % 4];
        var pixels = Enumerable.Range(edge.Start, edge.Count);
        return (edge.Reversed ? pixels.Reverse() : pixels).ToArray();
    }

    // Traverse each neighboring square in the opposite direction. Begin/end near the
    // neighboring edges, so the single dot crosses between square perimeter loops.
    public static List<(string BoardId, int Pixel)> DotPath(IReadOnlyList<BoardSettings> boards)
    {
        var path = new List<(string, int)>();
        var assigned = boards.Where(b => b.DeviceMac is not null).ToList();
        for (var i = 0; i < assigned.Count; i++)
        {
            var b = assigned[i];
            var perimeter = Enumerable.Range(0, 4).SelectMany(e => EdgePixels(b, e)).ToArray();
            var join = EdgePixels(b, i == 0 ? 1 : 3);
            var origin = Array.IndexOf(perimeter, join[join.Length / 2]);
            for (var p = 0; p < perimeter.Length; p++)
                path.Add((b.Id, perimeter[(origin + (i % 2 == 0 ? p : -p) + perimeter.Length) % perimeter.Length]));
            // An interior square is entered on its left and must leave on its right.
            // Continue along its perimeter after the full loop to the handoff point.
            if (i > 0 && i < assigned.Count - 1)
            {
                var exit = EdgePixels(b, 1);
                var destination = Array.IndexOf(perimeter, exit[exit.Length / 2]);
                for (var p = 0; p < perimeter.Length; p++)
                {
                    var current = (origin + (i % 2 == 0 ? p : -p) + perimeter.Length) % perimeter.Length;
                    path.Add((b.Id, perimeter[current]));
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
        for (var edge = 0; edge < 4; edge++)
        {
            var pixels = EdgePixels(board, edge);
            for (var p = 0; p < pixels.Length; p++)
            {
                var fraction = (p + 0.5) / pixels.Length;
                var x = index + (edge == 0 ? fraction : edge == 1 ? 1 : edge == 2 ? 1 - fraction : 0);
                var at = new BoardLightShow.Spot(x, boards.Count, edge, index, (edge + fraction) / 4);
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
