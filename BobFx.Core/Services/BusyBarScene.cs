namespace BobFx.Core.Services;

internal readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb operator *(Rgb color, double gain) => new(color.R * gain, color.G * gain, color.B * gain);
    public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    public static Rgb Mix(Rgb a, Rgb b, double amount) => a * (1 - amount) + b * amount;
}

/// <summary>72x16 floating-point frame. Out-of-range coordinates are ignored so layers can overhang.</summary>
internal sealed class BusyBarCanvas
{
    public const int Width = BusyBarAnimFile.Width, Height = BusyBarAnimFile.Height;
    private readonly Rgb[] pixels = new Rgb[Width * Height];
    private static bool Inside(int x, int y) => x is >= 0 and < Width && y is >= 0 and < Height;
    public Rgb this[int x, int y]
    {
        get => Inside(x, y) ? pixels[y * Width + x] : default;
        set { if (Inside(x, y)) pixels[y * Width + x] = value; }
    }
    public void Blend(int x, int y, Rgb color, double alpha) => this[x, y] = Rgb.Mix(this[x, y], color, Math.Clamp(alpha, 0, 1));
    public void Add(int x, int y, Rgb color) => this[x, y] += color;
    public void Scale(int x, int y, double gain) => this[x, y] *= gain;
    public void Fill(Rgb color) => Array.Fill(pixels, color);
    public byte[] ToBgr()
    {
        var bytes = new byte[BusyBarAnimFile.FrameBytes];
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
        for (var p = 0; p < pixels.Length; p++)
        {
            bytes[p * 3] = Channel(pixels[p].B); bytes[p * 3 + 1] = Channel(pixels[p].G); bytes[p * 3 + 2] = Channel(pixels[p].R);
        }
        return bytes;
    }
}

/// <summary>Layers of the neon sunset scene, matching the web Game page (frontend/src/lib/sunset.ts and
/// style.css): ink-to-purple sky, sliced sun, pink horizon, magenta grid floor and cyan-tipped ridges.
/// Every animated layer takes a loop phase or a whole number of cycles so loops close without a seam.</summary>
internal static class BusyBarScene
{
    private const int Width = BusyBarCanvas.Width, Height = BusyBarCanvas.Height;
    internal const int Horizon = 9;
    internal const double Centre = 35.5;

    internal static readonly Rgb Ink = new(16, 9, 30), Purple = new(48, 16, 68), Pink = new(255, 79, 173),
        Orange = new(255, 173, 102), Violet = new(140, 80, 255), Mint = new(150, 237, 223), White = new(255, 255, 255),
        Magenta = new(224, 26, 138), Cyan = new(31, 148, 217), SunYellow = new(255, 245, 31), SunAmber = new(255, 204, 32),
        SunRed = new(255, 77, 31), SunPink = new(255, 56, 150);
    private static readonly Rgb Haze = new(0, 18, 22), Star = new(255, 230, 250), SunGlow = new(255, 20, 64), Rock = new(20, 8, 30);

    internal static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    internal static double Smooth(double value) { var v = Clamp01(value); return v * v * (3 - 2 * v); }
    internal static double Frac(double value) => value - Math.Floor(value);

    internal static Rgb Ramp(double at, params (double At, Rgb Color)[] stops)
    {
        if (at <= stops[0].At) return stops[0].Color;
        for (var i = 1; i < stops.Length; i++)
            if (at <= stops[i].At)
                return Rgb.Mix(stops[i - 1].Color, stops[i].Color, (at - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
        return stops[^1].Color;
    }

    internal static Rgb SunColor(double v) => Ramp(v, (0.10, SunYellow), (0.30, SunAmber), (0.52, SunRed), (0.85, SunPink));

    internal static void Sky(BusyBarCanvas canvas)
    {
        for (var y = 0; y < Height; y++)
        {
            var color = y < Horizon ? Rgb.Mix(Ink, Purple, y / (Horizon - 1d)) + Haze * (y < 6 ? 0 : (y - 5) / 3d) : Ink * 0.7;
            for (var x = 0; x < Width; x++) canvas[x, y] = color;
        }
    }

    private static readonly (int X, int Y)[] StarPositions = [(3, 1), (9, 4), (15, 0), (21, 3), (27, 1), (45, 0), (50, 3), (57, 1), (63, 3), (69, 1)];
    internal static void Stars(BusyBarCanvas canvas, double phase)
    {
        for (var i = 0; i < StarPositions.Length; i++)
        {
            var twinkle = 0.5 + 0.5 * Math.Sin(2 * Math.PI * ((1 + i % 3) * phase + i * 0.37));
            canvas.Blend(StarPositions[i].X, StarPositions[i].Y, Star, 0.7 * (0.35 + 0.65 * twinkle));
        }
    }

    /// <summary>Retro sun clipped at the horizon. Two gaps drift down its lower half, widening as they sink;
    /// <paramref name="slice"/> advances by a whole number over a loop.</summary>
    internal static void Sun(BusyBarCanvas canvas, double centreY, double radius, double slice, double gain = 1)
    {
        const int across = 3, down = 8;
        for (var y = 0; y < Horizon; y++)
            for (var x = 0; x < Width; x++)
            {
                var distance = Math.Sqrt((x - Centre) * (x - Centre) + (y - centreY) * (y - centreY));
                if (distance > radius + 8) continue;
                if (distance > radius) canvas.Add(x, y, SunGlow * (0.22 * gain * Math.Exp(-(distance - radius) / 2)));
                if (distance > radius + 1) continue;
                Rgb sum = default; var lit = 0;
                for (var sy = 0; sy < down; sy++)
                    for (var sx = 0; sx < across; sx++)
                    {
                        var px = x - 0.5 + (sx + 0.5) / across; var py = y - 0.5 + (sy + 0.5) / down;
                        if (py > Horizon - 0.5 || (px - Centre) * (px - Centre) + (py - centreY) * (py - centreY) > radius * radius) continue;
                        var v = (py - (centreY - radius)) / radius;
                        // A gap darkens the sun rather than cutting through it, so a drifting gap shades rows gently.
                        sum += SunColor(v) * (InSunGap(v, slice) ? 0.35 : 1); lit++;
                    }
                if (lit > 0) canvas.Blend(x, y, sum * (gain / lit), lit / (double)(across * down));
            }
    }
    private static bool InSunGap(double v, double slice)
    {
        for (var j = 0; j < 2; j++)
        {
            var depth = Frac((j + slice) / 2);
            if (Math.Abs(v - (0.5 + depth * 0.5)) < (0.05 + 0.08 * depth) / 2) return true;
        }
        return false;
    }

    internal static void HorizonLine(BusyBarCanvas canvas, Rgb color, double gain = 1)
    {
        for (var x = 0; x < Width; x++)
        {
            canvas[x, Horizon] = color * gain;
            canvas.Add(x, Horizon - 1, color * (0.25 * gain));
            canvas.Add(x, Horizon + 1, color * (0.30 * gain));
        }
    }

    /// <summary>Perspective floor: fixed verticals fanning out from the centre and horizontals that run
    /// toward the viewer. <paramref name="scroll"/> counts grid cells travelled.</summary>
    internal static void Grid(BusyBarCanvas canvas, double scroll)
    {
        var travel = Frac(scroll);
        for (var y = Horizon + 1; y < Height; y++)
        {
            var depth = y - Horizon; // 1 at the horizon, 6 at the front edge
            var across = 0d;
            for (var line = 0; line < 4; line++)
            {
                // Each line doubles its distance from the horizon per cell travelled, so it speeds up toward
                // the viewer. Lines closer than a row apart would merge into a band; fade them in instead.
                var row = 6.5 * Math.Pow(2, travel - line);
                across = Math.Max(across, Clamp01(1.5 - 2 * Math.Abs(depth - row)) * Clamp01((row - 1) / 1.5));
            }
            for (var x = 0; x < Width; x++)
            {
                // Verticals leave the vanishing point one, three, five... columns apart per row of depth.
                var cell = (Math.Abs(x - Centre) + 0.5) / (2 * depth) - 0.5;
                var along = 1 - Math.Abs(cell - Math.Round(cell)) * 2 * depth;
                var coverage = Math.Max(Clamp01(across), 0.7 * Clamp01(along));
                canvas.Add(x, y, Magenta * (coverage * (0.35 + 0.65 * (depth - 1) / 5)));
            }
        }
    }

    private static readonly (double X, double Height)[] LeftRidge = [(0, 3), (4, 6), (8, 3.5), (12, 4.5), (15, 2), (18, 3), (24, 0)];
    private static readonly (double X, double Height)[] RightRidge = [(47, 0), (52, 3), (55, 2), (58, 6.5), (63, 4), (67, 5.5), (71, 3)];
    internal static void Mountains(BusyBarCanvas canvas) { Ridge(canvas, LeftRidge); Ridge(canvas, RightRidge); }
    private static void Ridge(BusyBarCanvas canvas, (double X, double Height)[] points)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var (x0, h0) = points[i - 1]; var (x1, h1) = points[i];
            var slope = Math.Abs((h1 - h0) / (x1 - x0));
            for (var x = (int)x0; x < x1 || (i == points.Length - 1 && x <= x1); x++)
            {
                var height = h0 + (h1 - h0) * (x - x0) / (x1 - x0);
                var top = Horizon - height;
                var edge = Rgb.Mix(Magenta, Cyan, Clamp01(height / 6)) * 0.85;
                for (var y = 0; y < Horizon; y++)
                {
                    canvas.Blend(x, y, Rock, y + 0.5 - top);
                    // A steep ridge spans several rows per column; light all of them so the line stays joined.
                    canvas.Blend(x, y, edge, 1 - Math.Max(0, Math.Abs(y - top) - slope / 2));
                }
            }
        }
    }

    internal static void Vignette(BusyBarCanvas canvas)
    {
        for (var x = 0; x < Width; x++)
        {
            var gain = 1 - 0.35 * Math.Pow((x - Centre) / 36, 4);
            for (var y = 0; y < Height; y++) canvas.Scale(x, y, gain);
        }
    }

    /// <summary>Darkens a rectangle (inclusive bounds) to <paramref name="gain"/>, easing out over <paramref name="soft"/> pixels.</summary>
    internal static void Dim(BusyBarCanvas canvas, int left, int right, int top, int bottom, double gain, double soft)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var outside = Math.Max(Math.Max(left - x, x - right), Math.Max(top - y, y - bottom));
                canvas.Scale(x, y, 1 - (1 - gain) * Clamp01(1 - outside / soft));
            }
    }

    /// <summary>Clockwise walk round the 172 border pixels, starting top left.</summary>
    internal const int PerimeterLength = 172;
    internal static (int X, int Y) Perimeter(int index)
    {
        var p = (index % PerimeterLength + PerimeterLength) % PerimeterLength;
        return p < 72 ? (p, 0) : p < 86 ? (71, p - 71) : p < 158 ? (157 - p, 15) : (0, 172 - p);
    }

    // Ten rows of bold capitals with two-pixel strokes: the device fonts are too thin to read across a room.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['B'] = ["######..", "#######.", "##...##.", "##...##.", "#######.", "########", "##....##", "##....##", "########", "#######."],
        ['O'] = [".######.", "########", "##....##", "##....##", "##....##", "##....##", "##....##", "##....##", "########", ".######."],
        ['F'] = ["########", "########", "##......", "##......", "######..", "######..", "##......", "##......", "##......", "##......"],
        ['X'] = ["##....##", "##....##", ".##..##.", ".##..##.", "..####..", "..####..", ".##..##.", ".##..##.", "##....##", "##....##"],
        ['G'] = [".######.", "########", "##......", "##......", "##..####", "##..####", "##....##", "##....##", "########", ".######."],
        ['0'] = [".######.", "########", "##....##", "##....##", "##....##", "##....##", "##....##", "##....##", "########", ".######."],
        ['1'] = ["..###...", ".####...", ".####...", "...##...", "...##...", "...##...", "...##...", "...##...", ".######.", ".######."],
        ['2'] = [".######.", "########", "##....##", "......##", ".....###", "...####.", ".####...", "###.....", "########", "########"],
        ['3'] = ["#######.", "########", "......##", "......##", "..#####.", "..#####.", "......##", "......##", "########", "#######."],
        ['4'] = ["##....##", "##....##", "##....##", "##....##", "########", "########", "......##", "......##", "......##", "......##"],
        ['5'] = ["########", "########", "##......", "##......", "#######.", "########", "......##", "......##", "########", "#######."],
        ['6'] = [".######.", "########", "##......", "##......", "#######.", "########", "##....##", "##....##", "########", ".######."],
        ['7'] = ["########", "########", "......##", ".....###", "....###.", "...###..", "...##...", "...##...", "...##...", "...##..."],
        ['8'] = [".######.", "########", "##....##", "##....##", ".######.", "########", "##....##", "##....##", "########", ".######."],
        ['9'] = [".######.", "########", "##....##", "##....##", "########", ".#######", "......##", "......##", "########", ".######."],
        ['!'] = ["##", "##", "##", "##", "##", "##", "##", "..", "##", "##"],
        // Narrow capitals, six columns wide, for lines too long for the wide ones. Keyed in lower case.
        ['g'] = [".####.", "######", "##....", "##....", "##.###", "##.###", "##..##", "##..##", "######", ".####."],
        ['a'] = [".####.", "######", "##..##", "##..##", "######", "######", "##..##", "##..##", "##..##", "##..##"],
        ['m'] = ["##..##", "######", "######", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##"],
        ['e'] = ["######", "######", "##....", "##....", "#####.", "#####.", "##....", "##....", "######", "######"],
        ['o'] = [".####.", "######", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "######", ".####."],
        ['v'] = ["##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", ".####.", ".####.", "..##.."],
        ['r'] = ["#####.", "######", "##..##", "##..##", "######", "#####.", "##.##.", "##..##", "##..##", "##..##"],
        ['_'] = ["..", "..", "..", "..", "..", "..", "..", "..", "..", ".."],
        [' '] = ["...", "...", "...", "...", "...", "...", "...", "...", "...", "..."]
    };
    internal const int LogoTop = 3, LogoBottom = 12;
    internal static readonly bool[,] Brand = Lettering("BOB FX", 1), Go = Lettering("GO!", 2), GameOver = Lettering("game_over", 1);

    /// <summary>Centred lettering as a [x, y] mask on rows 3..12.</summary>
    private static bool[,] Lettering(string text, int gap)
    {
        var mask = new bool[Width, Height];
        var x = (Width - (text.Sum(c => Glyphs[c][0].Length) + gap * (text.Length - 1))) / 2;
        foreach (var rows in text.Select(c => Glyphs[c]))
        {
            for (var row = 0; row < rows.Length; row++)
                for (var col = 0; col < rows[row].Length; col++)
                    mask[x + col, LogoTop + row] = rows[row][col] == '#';
            x += rows[0].Length + gap;
        }
        return mask;
    }

    internal static Rgb BrandColor(int y) =>
        Ramp((y - LogoTop) / (double)(LogoBottom - LogoTop), (0, SunYellow), (0.35, SunAmber), (0.7, SunRed), (1, SunPink));

    /// <summary>Dusk colours for the end of a game: the Game page's soft pink title sinking into violet.</summary>
    internal static Rgb DuskColor(int y) =>
        Ramp((y - LogoTop) / (double)(LogoBottom - LogoTop), (0, new Rgb(255, 156, 189)), (0.55, Pink), (1, Violet));

    /// <summary>Draws lettering with a dark outline. <paramref name="coverage"/> is 0..1 per letter pixel.</summary>
    internal static void Letters(BusyBarCanvas canvas, bool[,] mask, Func<int, int, double> coverage, Func<int, int, Rgb> fill)
    {
        double At(int x, int y) => x is >= 0 and < Width && y is >= 0 and < Height && mask[x, y] ? coverage(x, y) : 0;
        for (var y = LogoTop - 1; y <= LogoBottom + 1; y++)
            for (var x = 0; x < Width; x++)
            {
                if (mask[x, y]) continue;
                var near = 0d;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++) near = Math.Max(near, At(x + dx, y + dy));
                canvas.Scale(x, y, 1 - 0.88 * near);
            }
        for (var y = LogoTop; y <= LogoBottom; y++)
            for (var x = 0; x < Width; x++)
                if (mask[x, y]) canvas.Blend(x, y, fill(x, y), coverage(x, y));
    }

    /// <summary>A number in the bold lettering, centred on the display and scaled about its centre.
    /// Edges are anti-aliased so it can grow and shrink smoothly. <paramref name="whiten"/> 0..1 bleaches the fill.</summary>
    internal static void Number(BusyBarCanvas canvas, string digits, double scale, double alpha, double whiten)
    {
        const int samples = 3, glyphHeight = 10, pitch = 9; // eight columns plus a one-column gap
        var span = digits.Length * pitch - 1;
        bool Solid(double gx, double gy)
        {
            if (gx < 0 || gx >= span || gy < 0 || gy >= glyphHeight) return false;
            var column = (int)gx % pitch;
            return column < 8 && Glyphs[digits[(int)gx / pitch]][(int)gy][column] == '#';
        }
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                        if (Solid((x - 0.5 + (sx + 0.5) / samples - Centre) / scale + span / 2d, (y - 0.5 + (sy + 0.5) / samples - 7.5) / scale + glyphHeight / 2d)) hits++;
                if (hits == 0) continue;
                var shade = Clamp01(0.5 + (y - 7.5) / (glyphHeight * scale));
                var fill = Ramp(shade, (0, SunYellow), (0.35, SunAmber), (0.7, SunRed), (1, SunPink));
                canvas.Blend(x, y, Rgb.Mix(fill, White, whiten), alpha * hits / (samples * samples));
            }
    }

    /// <summary>Lettering scaled about the centre of the display, with anti-aliased edges, for a pop-in.
    /// <paramref name="whiten"/> 0..1 bleaches the fill.</summary>
    internal static void ScaledLetters(BusyBarCanvas canvas, bool[,] mask, double scale, double whiten, Func<int, Rgb> fill)
    {
        const int samples = 3;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                    {
                        var mx = (int)Math.Floor((x - 0.5 + (sx + 0.5) / samples - Centre) / scale + Centre + 0.5);
                        var my = (int)Math.Floor((y - 0.5 + (sy + 0.5) / samples - 7.5) / scale + 7.5 + 0.5);
                        if (mx is >= 0 and < Width && my is >= 0 and < Height && mask[mx, my]) hits++;
                    }
                if (hits == 0) continue;
                var row = Math.Clamp((int)Math.Round((y - 7.5) / scale + 7.5), LogoTop, LogoBottom);
                canvas.Blend(x, y, Rgb.Mix(fill(row), White, whiten), hits / (double)(samples * samples));
            }
    }

    /// <summary>Stable pseudo-random value 0..1 for a pixel, so debris moves the same way in every frame.</summary>
    internal static double Scatter(int x, int y) => ((x * 73856093 ^ y * 19349663) & 0xFFFF) / 65536d;

    /// <summary>Diagonal white glint crossing the lettering as <paramref name="progress"/> runs 0..1.</summary>
    internal static void Shine(BusyBarCanvas canvas, bool[,] mask, double progress)
    {
        var centre = -8 + progress * (Width + 16);
        for (var y = LogoTop; y <= LogoBottom; y++)
            for (var x = 0; x < Width; x++)
                if (mask[x, y]) canvas.Blend(x, y, White, 0.7 * Clamp01(2 - Math.Abs(x + (y - 7.5) * 0.6 - centre)));
    }
}
