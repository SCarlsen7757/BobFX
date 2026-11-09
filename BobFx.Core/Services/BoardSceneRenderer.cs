using System.Numerics;

namespace BobFx.Core.Services;

public static class BoardSceneRenderer
{
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
        for (var i = 0; i < boards.Count; i++)
        {
            var b = boards[i];
            var perimeter = Enumerable.Range(0, 4).SelectMany(e => EdgePixels(b, e)).ToArray();
            var join = EdgePixels(b, i == 0 ? 1 : 3);
            var origin = Array.IndexOf(perimeter, join[join.Length / 2]);
            for (var p = 0; p < perimeter.Length; p++)
                path.Add((b.Id, perimeter[(origin + (i % 2 == 0 ? p : -p) + perimeter.Length) % perimeter.Length]));
            // An interior square is entered on its left and must leave on its right.
            // Continue along its perimeter after the full loop to the handoff point.
            if (i > 0 && i < boards.Count - 1)
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
        var index = settings.Boards.FindIndex(b => b.Id == board.Id);
        var total = settings.Boards.Count;
        void Set(int pixel, double r, double g, double b)
        {
            if (pixel < 0 || pixel >= count) return;
            output[pixel * 3] = (byte)Math.Clamp(r * 255, 0, 255);
            output[pixel * 3 + 1] = (byte)Math.Clamp(g * 255, 0, 255);
            output[pixel * 3 + 2] = (byte)Math.Clamp(b * 255, 0, 255);
        }
        if (phase == GamePhase.Ending && elapsed >= settings.Animation.FlashSeconds)
        {
            var path = DotPath(settings.Boards);
            if (path.Count > 0)
            {
                var position = (int)((elapsed - settings.Animation.FlashSeconds) * settings.Animation.DotLapsPerSecond * path.Count) % path.Count;
                var dot = path[position];
                if (dot.BoardId == board.Id) Set(dot.Pixel, 1, 0, 0);
            }
            return output;
        }
        for (var edge = 0; edge < 4; edge++)
        {
            var pixels = EdgePixels(board, edge);
            for (var p = 0; p < pixels.Length; p++)
            {
                var fraction = (p + 0.5) / pixels.Length;
                var x = index + (edge == 0 ? fraction : edge == 1 ? 1 : edge == 2 ? 1 - fraction : 0);
                if (phase == GamePhase.PreCountdown)
                {
                    var progress = preparationSeconds > 0 ? elapsed / preparationSeconds : 1;
                    var lit = progress < 0.6 ? Math.Abs(x - progress / 0.6 * total) < 0.15
                        : progress < 0.8 ? edge is 0 or 2 : true;
                    if (lit) Set(pixels[p], 0, 0, 1);
                }
                else if (phase == GamePhase.Running)
                {
                    if (elapsed < 0.75) Set(pixels[p], 0, 1, 0);
                    else
                    {
                        var wave = (Math.Sin(x * Math.PI * 2 - elapsed * 3) + 1) / 2;
                        var pulse = Math.Pow(Math.Max(0, Math.Cos(x * 2 - elapsed * 4)), 12);
                        Set(pixels[p], pulse, 0.15 + 0.6 * wave + 0.25 * pulse, 0.25 + 0.6 * (1 - wave) + 0.15 * pulse);
                    }
                }
                else if (phase == GamePhase.Ending && (int)(elapsed / settings.Animation.FlashSeconds * 6) % 2 == 0)
                    Set(pixels[p], 1, 0, 0);
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
