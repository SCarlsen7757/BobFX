using System.Numerics;
using System.Runtime.CompilerServices;

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

    // Settings are replaced, never changed, so what was worked out for one instance stays true for it. An entry
    // goes away with its settings.
    private static readonly ConditionalWeakTable<InstallationSettings, BoardScene> Scenes = new();
    /// <summary>What the frames of every board share for these settings, worked out once instead of for each
    /// board of each frame.</summary>
    public static BoardScene Scene(InstallationSettings settings) => Scenes.GetValue(settings, static s => new BoardScene(s));

    public static byte[] Render(InstallationSettings settings, BoardSettings board, int count, GamePhase phase, double elapsed, double preparationSeconds) =>
        Scene(settings).Render(board, count, phase, elapsed, preparationSeconds);

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

    /// <summary>The boards that take part in the show, in row order, with what each needs to be drawn.</summary>
    public sealed class BoardScene
    {
        private readonly InstallationSettings settings;
        private readonly LightPalette colors;
        private readonly Dictionary<string, int> positions = [];
        private readonly Dictionary<string, DeviceSettings> devices;
        private readonly (int[] Left, int[] Right)[] strips;
        // The comet's path through all boards, split by board: where on the path each of a board's LEDs lies.
        private readonly Dictionary<string, (int Step, int Pixel)[]> pathSteps;
        private readonly int pathLength;

        /// <summary>Boards with a selected controller, in the order of the row.</summary>
        public IReadOnlyList<BoardSettings> Boards { get; }

        internal BoardScene(InstallationSettings settings)
        {
            this.settings = settings;
            Boards = AssignedBoards(settings);
            devices = settings.Devices.GroupBy(d => d.Mac).ToDictionary(group => group.Key, group => group.First());
            colors = LightPalette.For(settings.LightShow);
            strips = new (int[], int[])[Boards.Count];
            for (var i = 0; i < Boards.Count; i++)
            {
                positions[Boards[i].Id] = i;
                strips[i] = (BoardMapping.StripPixels(Boards[i], BoardMapping.Left), BoardMapping.StripPixels(Boards[i], BoardMapping.Right));
            }
            var path = DotPath(Boards);
            pathLength = path.Count;
            pathSteps = path.Select((entry, step) => (entry.BoardId, Step: step, entry.Pixel)).GroupBy(entry => entry.BoardId)
                .ToDictionary(group => group.Key, group => group.Select(entry => (entry.Step, entry.Pixel)).ToArray());
        }

        /// <summary>The controller a board of this scene is on, as saved.</summary>
        public DeviceSettings Device(BoardSettings board) => devices[board.DeviceMac!];

        /// <summary>One frame of the show for one board: RGB for each of its controller's <paramref name="count"/>
        /// LEDs. A board that is not part of the scene, or any board while idle, is dark.</summary>
        public byte[] Render(BoardSettings board, int count, GamePhase phase, double elapsed, double preparationSeconds)
        {
            var output = new byte[count * 3];
            if (phase == GamePhase.Idle || !positions.TryGetValue(board.Id, out var index)) return output;
            var show = settings.LightShow;
            var timing = settings.Animation;
            var gain = show.Brightness / 100d;
            byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * gain), 0, 255);
            // The second part of the ending: a comet runs the dot path over a glow that sinks to black.
            // For each LED the comet has passed, how far behind its head the LED is.
            double[]? trail = null;
            Rgb glow = default;
            double since = 0, tail = 1;
            if (phase == GamePhase.Ending && elapsed >= timing.FlashSeconds)
            {
                since = elapsed - timing.FlashSeconds;
                glow = BoardLightShow.Afterglow(colors, since, timing.DotSeconds);
                (var head, tail) = BoardLightShow.Comet(since, timing);
                trail = new double[count];
                Array.Fill(trail, double.PositiveInfinity);
                foreach (var (step, pixel) in pathSteps.GetValueOrDefault(board.Id, []))
                {
                    if (pixel < 0 || pixel >= count) continue;
                    // An interior board is on the path twice; each pixel keeps its position nearest the head.
                    var behind = BusyBarScene.Frac(head - step / (double)pathLength);
                    if (behind < tail && behind < trail[pixel]) trail[pixel] = behind;
                }
            }
            // The caller's board is drawn as given: it may be a draft of the board at this place in the row.
            var (left, right) = ReferenceEquals(board, Boards[index]) ? strips[index]
                : (BoardMapping.StripPixels(board, BoardMapping.Left), BoardMapping.StripPixels(board, BoardMapping.Right));
            for (var side = 0; side < 2; side++)
            {
                var pixels = side == BoardMapping.Left ? left : right;
                var x = index + (side == BoardMapping.Left ? StripInset : 1 - StripInset);
                for (var p = 0; p < pixels.Length; p++)
                {
                    var pixel = pixels[p];
                    if (pixel < 0 || pixel >= count) continue;
                    var along = (p + 0.5) / pixels.Length;
                    var at = new BoardLightShow.Spot(x, Boards.Count, side, index, along, side == BoardMapping.Left ? along / 2 : 1 - along / 2);
                    var color = phase switch
                    {
                        GamePhase.PreCountdown => BoardLightShow.Countdown(colors, at, elapsed, preparationSeconds),
                        GamePhase.Running => BoardLightShow.Running(colors, show, at, elapsed),
                        _ when trail is null => BoardLightShow.Flare(colors, at, elapsed, timing.FlashSeconds),
                        _ => double.IsPositiveInfinity(trail[pixel]) ? glow
                            : BoardLightShow.CometPixel(colors, glow, trail[pixel] / tail, since, timing.DotSeconds)
                    };
                    output[pixel * 3] = Channel(color.R); output[pixel * 3 + 1] = Channel(color.G); output[pixel * 3 + 2] = Channel(color.B);
                }
            }
            return output;
        }
    }
}
