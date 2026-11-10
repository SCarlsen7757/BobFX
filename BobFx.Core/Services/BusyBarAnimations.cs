using System.Collections.Concurrent;
using System.Security.Cryptography;
using static BobFx.Core.Services.BusyBarScene;

namespace BobFx.Core.Services;

internal enum BusyBarScreen { Idle, Countdown, Go, Running, Ending }
internal sealed record BusyBarAnimation(string Name, byte[] Data);

/// <summary>Native 72x16 neon sunset animations, one per game screen.
/// Frames are generated locally; only uploaded assets and playback commands cross Wi-Fi.</summary>
internal static class BusyBarAnimations
{
    internal static BusyBarScreen Screen(GamePhase phase, double elapsed, AnimationSettings animation) => phase switch
    {
        GamePhase.PreCountdown => BusyBarScreen.Countdown,
        GamePhase.Running => elapsed < GoSeconds ? BusyBarScreen.Go : BusyBarScreen.Running,
        GamePhase.Ending => BusyBarScreen.Ending,
        _ => BusyBarScreen.Idle
    };

    internal const double GoSeconds = 0.75;

    /// <summary>Screens that play through once; every other screen loops until replaced.</summary>
    internal static bool PlaysOnce(BusyBarScreen screen) => screen is BusyBarScreen.Countdown or BusyBarScreen.Go or BusyBarScreen.Ending;

    /// <summary>Length of one loop, or of the whole clip for play-once screens.</summary>
    internal static double Seconds(BusyBarScreen screen, AnimationSettings timing, int preparation) => screen switch
    {
        BusyBarScreen.Idle => 8,
        BusyBarScreen.Running => 4,
        // The whole preparation is one clip with its seconds baked in, so the bar counts down by itself.
        BusyBarScreen.Countdown => Math.Max(1, preparation),
        // The burst is over within GoSeconds; the tail holds the last frame in case the next command is late.
        BusyBarScreen.Go => GoSeconds * 2,
        // One clip for the whole ending: three flares while the boards flash, then the sun sets
        // while their dot runs.
        _ => timing.FlashSeconds + timing.DotSeconds
    };

    /// <summary>Rates whose frame time is a whole number of milliseconds, so a long clip keeps time on the bar.
    /// bicycle0 stores every frame whole, so its long clips run at half rate to stay small.</summary>
    internal static int Fps(BusyBarScreen screen, BusyBarAnimFormat format) =>
        screen == BusyBarScreen.Go ? 30 : format == BusyBarAnimFormat.Bicycle1 ? 20 : 10;

    /// <summary>The countdown clip can be joined at any whole second: section "from3" starts three seconds in.</summary>
    internal static string CountdownSection(double elapsed, int preparation, BusyBarAnimFormat format)
    {
        var second = Math.Clamp((int)elapsed, 0, Math.Max(1, preparation) - 1);
        return second == 0 || format == BusyBarAnimFormat.Bicycle0 ? "default" : $"from{second}";
    }

    private static readonly ConcurrentDictionary<(BusyBarScreen, AnimationSettings, BusyBarAnimFormat, int), BusyBarAnimation> Cache = new();

    /// <summary>All five assets for a preparation of <paramref name="preparation"/> whole seconds.</summary>
    internal static IReadOnlyDictionary<BusyBarScreen, BusyBarAnimation> Create(AnimationSettings timing, BusyBarAnimFormat format, int preparation)
    {
        if (Cache.Count >= 64) Cache.Clear();
        return Enum.GetValues<BusyBarScreen>().ToDictionary(s => s, s =>
            Cache.GetOrAdd((s, timing, format, s == BusyBarScreen.Countdown ? preparation : 0), _ => Create(s, timing, format, preparation)));
    }

    private static BusyBarAnimation Create(BusyBarScreen screen, AnimationSettings timing, BusyBarAnimFormat format, int preparation)
    {
        var fps = Fps(screen, format);
        var count = Math.Max(1, (int)Math.Round(Seconds(screen, timing, preparation) * fps));
        var frames = Enumerable.Range(0, count).Select(i => Render(screen, (double)i / fps, timing, preparation)).ToArray();
        var sections = screen == BusyBarScreen.Countdown
            ? Enumerable.Range(1, count / fps - 1).Select(second => ($"from{second}", second * fps)).ToArray() : [];
        var data = BusyBarAnimFile.Encode(frames, fps, format, sections);
        var hash = Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();
        var label = screen == BusyBarScreen.Countdown ? $"countdown{Math.Max(1, preparation)}" : screen.ToString().ToLowerInvariant();
        return new($"bobfx-{label}-{(format == BusyBarAnimFormat.Bicycle1 ? "b1" : "b0")}-{hash}.anim", data);
    }

    /// <summary>One 72x16 BGR frame of <paramref name="screen"/>, <paramref name="elapsed"/> seconds into it.</summary>
    internal static byte[] Render(BusyBarScreen screen, double elapsed, AnimationSettings timing, int preparation = 5)
    {
        var canvas = new BusyBarCanvas();
        var phase = Frac(elapsed / Seconds(screen, timing, preparation));
        switch (screen)
        {
            case BusyBarScreen.Idle: Idle(canvas, phase); break;
            case BusyBarScreen.Running: Running(canvas, phase); break;
            case BusyBarScreen.Countdown: Countdown(canvas, elapsed, Math.Max(1, preparation)); break;
            case BusyBarScreen.Go: Go(canvas, elapsed * 30); break;
            default: Ending(canvas, elapsed, timing); break;
        }
        return canvas.ToBgr();
    }

    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    private const double SunHidden = 17.5; // Centre row at which the sun is wholly below the horizon.

    /// <summary>Eight-second story: the sun rises over the scrolling grid, BOB FX wipes in behind a mint
    /// edge, a glint crosses it, the logo dissolves and the sun sets again.</summary>
    private static void Idle(BusyBarCanvas canvas, double phase)
    {
        var t = phase * 8;
        var risen = Smooth(t / 2) * (1 - Smooth((t - 6.6) / 1.4));
        Sky(canvas); Stars(canvas, phase);
        Sun(canvas, SunHidden - (SunHidden - Horizon) * risen, 7.5, 2 * phase); // Slow slices: fast ones read as flicker on so few rows.
        Mountains(canvas); HorizonLine(canvas, Pink); Grid(canvas, 8 * phase); Vignette(canvas);
        var wipe = Clamp01(t - 2.5); var dissolve = Clamp01(t - 6);
        if (wipe <= 0 || dissolve >= 1) return;
        Dim(canvas, 11, 60, 2, 13, 1 - 0.5 * Smooth(wipe) * (1 - Smooth(dissolve)), 5);
        var edge = 10 + wipe * 52;
        Letters(canvas, Brand,
            (x, y) => x < edge && Bayer[y % 4 * 4 + x % 4] < (1 - dissolve) * 16 ? 1 : 0,
            (x, y) => wipe < 1 && x >= edge - 2 ? Mint : BrandColor(y));
        if (t is >= 4 and <= 4.8) Shine(canvas, Brand, (t - 4) / 0.8);
    }

    /// <summary>Four-second game loop: the logo stays up over a faster grid, a sun and horizon
    /// pulsing on the beat, and mint speed streaks at both ends in place of the mountains.</summary>
    private static void Running(BusyBarCanvas canvas, double phase)
    {
        var t = phase * 4;
        var beat = Math.Sin(2 * Math.PI * 8 * phase);
        Sky(canvas); Stars(canvas, phase);
        Sun(canvas, Horizon, 7.5 + 0.5 * beat, 8 * phase);
        HorizonLine(canvas, Pink, 0.85 + 0.15 * beat); Grid(canvas, 12 * phase);
        // Speed streaks instead of mountains: mint dashes racing out to both ends of the sky.
        foreach (var (row, offset) in new[] { (1, 0d), (3, 0.55), (5, 0.25), (7, 0.8) })
        {
            var head = Frac(8 * phase + offset) * 17 - 4;
            for (var k = 0; k < 5; k++)
            {
                var column = (int)Math.Floor(head) - k; // 0 nearest the centre, 9 at the display edge
                if (column is < 0 or > 9) continue;
                canvas.Blend(9 - column, row, Mint, 0.9 * (1 - k / 5d));
                canvas.Blend(62 + column, row, Mint, 0.9 * (1 - k / 5d));
            }
        }
        Vignette(canvas);
        Dim(canvas, 11, 60, 2, 13, 0.5, 5);
        Letters(canvas, Brand, (_, _) => 1, (_, y) => BrandColor(y) * (0.92 + 0.08 * beat));
        if (t is >= 1 and <= 1.7) Shine(canvas, Brand, (t - 1) / 0.7);
    }

    /// <summary>The whole preparation as one clip. Bold seconds in the sun colours pop in white-hot and
    /// oversized, settle, and fade out just before the next; the last second is 1. The border tells the
    /// stage, as on the boards: a scanner for the first 60%, the two long edges for 20%, then all four.
    /// The scene behind stays still and dim so the clip stays small and the number stays readable.</summary>
    private static void Countdown(BusyBarCanvas canvas, double elapsed, int preparation)
    {
        Sky(canvas); Stars(canvas, 0); Mountains(canvas); HorizonLine(canvas, Pink); Grid(canvas, 0); Vignette(canvas);
        Dim(canvas, 0, BusyBarCanvas.Width - 1, 0, BusyBarCanvas.Height - 1, 0.5, 1);
        var number = Math.Max(1, (int)Math.Ceiling(preparation - elapsed - 1e-9)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reach = (int)Math.Ceiling(number.Length * 9 * 1.3 / 2) + 3;
        Dim(canvas, 36 - reach, 35 + reach, 1, 14, 0.2, 4);
        var phase = Frac(elapsed / 2);
        if (elapsed < preparation * 0.6)
        {
            var centre = (Math.Sin(2 * Math.PI * 2 * phase - Math.PI / 2) + 1) * Centre;
            for (var x = 0; x < BusyBarCanvas.Width; x++)
            {
                var intensity = Math.Max(0, 1 - Math.Abs(x - centre) / 8);
                var color = Rgb.Mix(Pink, SunYellow, intensity * intensity);
                canvas.Blend(x, 0, color, intensity); canvas.Blend(x, 15, color, intensity);
            }
        }
        else if (elapsed < preparation * 0.8)
        {
            for (var x = 0; x < BusyBarCanvas.Width; x++)
                foreach (var (row, direction) in new[] { (0, 1), (15, -1) })
                {
                    var pulse = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (x / 12d - direction * 6 * phase));
                    canvas[x, row] = Rgb.Mix(Pink * 0.7, Orange, pulse * pulse);
                }
        }
        else
        {
            for (var index = 0; index < PerimeterLength; index++)
            {
                var (x, y) = Perimeter(index);
                canvas[x, y] = Ramp(Frac(index / 43d - 4 * phase), (0, Violet), (1 / 3d, Pink), (2 / 3d, Orange), (1, Violet));
            }
        }
        var beat = Frac(elapsed + 1e-9); // 0 as each second starts
        var pop = 1 - Smooth(beat / 0.25);
        Number(canvas, number, 1.3 * (1 + 0.25 * pop), 1 - 0.85 * Clamp01((beat - 0.85) / 0.15), 0.85 * (1 - Clamp01(beat / 0.3)));
    }

    private static readonly Rgb GoBackdrop = new(10, 36, 34);

    /// <summary>Mint burst: a flash, two bars racing out from the centre and GO! revealed between them.
    /// It settles into a still frame well inside GoSeconds.</summary>
    private static void Go(BusyBarCanvas canvas, double frame)
    {
        var backdrop = Rgb.Mix(GoBackdrop, Mint, Math.Pow(Clamp01(1 - frame / 9), 1.5));
        canvas.Fill(Rgb.Mix(backdrop, White, 0.3 * Clamp01(1 - frame / 3)));
        var reach = 2 + 4 * frame;
        for (var x = 0; x < BusyBarCanvas.Width; x++)
        {
            var distance = Math.Abs(x - Centre);
            if (distance <= reach) { canvas.Blend(x, 0, Mint, 0.8); canvas.Blend(x, 15, Mint, 0.8); }
            if (Math.Abs(distance - reach) <= 1.5)
                for (var y = 0; y < BusyBarCanvas.Height; y++) canvas.Blend(x, y, White, 0.9);
        }
        var reveal = 3 * (frame - 1);
        Letters(canvas, BusyBarScene.Go, (x, _) => Math.Abs(x - Centre) <= reveal ? 1 : 0,
            (_, y) => Rgb.Mix(White, Mint, (y - LogoTop) / (double)(LogoBottom - LogoTop)));
    }

    /// <summary>The game is over. GAME OVER slams in oversized and white-hot with the first of three sun flares,
    /// which keep the boards' flash rhythm: each lights the sky, sends a ring of sun colours outward and makes
    /// the lettering pulse, a little weaker each time. Then, while the boards run their dot, the sun sets and
    /// the lettering falls apart: its pixels drop away from the bottom rows up, fading as they fall, leaving
    /// the dusk the idle loop rises from.</summary>
    private static void Ending(BusyBarCanvas canvas, double elapsed, AnimationSettings timing)
    {
        var flare = 0d; var spread = 0d; var set = 0d;
        if (elapsed < timing.FlashSeconds)
        {
            // Same cadence as the boards: six equal intervals, lit on the even ones.
            var interval = timing.FlashSeconds / 6;
            var index = (int)(elapsed / timing.FlashSeconds * 6);
            if (index % 2 == 0)
            {
                spread = Clamp01((elapsed - index * interval) / interval);
                flare = Math.Pow(1 - spread, 1.5);
            }
        }
        else set = Smooth((elapsed - timing.FlashSeconds) / timing.DotSeconds);
        Sky(canvas); Stars(canvas, elapsed / 4);
        Sun(canvas, Horizon + (SunHidden - Horizon) * set, 7.5 + flare, 0, 0.75 + 0.5 * flare);
        Mountains(canvas); HorizonLine(canvas, Rgb.Mix(Pink, SunAmber, flare), 1 - 0.4 * set);
        Grid(canvas, 3 * (1 - (1 - set) * (1 - set))); // Coasts three cells and stops.
        Vignette(canvas);
        // Dim between flares and into dusk; a flare lifts the whole scene.
        Dim(canvas, 0, BusyBarCanvas.Width - 1, 0, BusyBarCanvas.Height - 1, 0.45 + 0.55 * flare, 1);
        var radius = 5 + 42 * spread;
        for (var y = 0; flare > 0 && y < BusyBarCanvas.Height; y++)
            for (var x = 0; x < BusyBarCanvas.Width; x++)
            {
                // A ring leaving the sun, flattened so it runs along the bar; hot core, cooler as it widens.
                var distance = Math.Sqrt((x - Centre) * (x - Centre) + (y - Horizon) * (y - Horizon) * 6.25);
                var ring = Clamp01(1 - Math.Abs(distance - radius) / 3);
                canvas.Blend(x, y, Ramp(spread, (0, White), (0.15, SunYellow), (0.5, Orange), (1, Pink)), 0.9 * ring * flare);
                canvas.Add(x, y, Pink * (0.25 * flare * (y < Horizon ? 1 : 0.4)));
            }
        var since = elapsed - timing.FlashSeconds;
        if (since < 0)
        {
            Dim(canvas, 5, 66, 2, 13, 0.4, 4);
            var pop = 1 - Smooth(elapsed / 0.25);
            if (pop > 0) { ScaledLetters(canvas, GameOver, 1 + 0.2 * pop, 0.9 * pop, DuskColor); return; }
            // Each flare is weaker than the last: the pulse is fading out.
            var pulse = flare * (1 - 0.25 * (int)(elapsed / timing.FlashSeconds * 3));
            Letters(canvas, GameOver, (_, _) => 1, (_, y) => Rgb.Mix(DuskColor(y) * (0.6 + 0.4 * pulse), White, 0.5 * pulse));
            return;
        }
        var length = timing.DotSeconds;
        Dim(canvas, 5, 66, 2, 13, 1 - 0.6 * (1 - Clamp01(since / (0.7 * length))), 4);
        var throb = 0.5 + 0.1 * Math.Cos(2 * Math.PI * 2 * since);
        for (var y = LogoTop; y <= LogoBottom; y++)
            for (var x = 0; x < BusyBarCanvas.Width; x++)
            {
                if (!GameOver[x, y]) continue;
                // Bottom rows let go first; within a row the order is scattered. Everything has let go by 60%
                // of the time and takes 30% to fall off the display.
                var fallen = (since - length * (0.05 + 0.35 * Scatter(x, y) + 0.2 * (LogoBottom - y) / 9d)) / (0.3 * length);
                if (fallen <= 0) canvas[x, y] = DuskColor(y) * throb;
                else if (fallen < 1)
                    canvas.Blend((int)Math.Round(x + (Scatter(y, x) - 0.5) * 6 * fallen), (int)Math.Round(y + 13 * fallen * fallen),
                        DuskColor(y) * throb, 1 - fallen);
            }
    }
}
