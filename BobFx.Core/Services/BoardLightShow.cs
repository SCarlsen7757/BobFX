using static BobFx.Core.Services.BusyBarScene;

namespace BobFx.Core.Services;

/// <summary>A built-in colour theme as the web interface shows it (#rrggbb screen colours).</summary>
public sealed record LightShowTheme(string Id, string Name, string Primary, string Secondary, string Tertiary, string Highlight, string Accent);

/// <summary>Theme colours converted for LED strips, with the gradients every phase shares.</summary>
internal sealed class LightPalette
{
    // Screen colours sent raw to a strip wash out to pastel. WLED leaves realtime data uncorrected
    // by default, so the palette is converted here; set to 1 if the controllers correct it themselves.
    private const double Gamma = 2.2;

    public Rgb Primary { get; }
    public Rgb Secondary { get; }
    public Rgb Tertiary { get; }
    public Rgb Highlight { get; }
    public Rgb Accent { get; }
    private readonly (double At, Rgb Color)[] loop, flare;

    private LightPalette(Rgb primary, Rgb secondary, Rgb tertiary, Rgb highlight, Rgb accent)
    {
        (Primary, Secondary, Tertiary, Highlight, Accent) = (primary, secondary, tertiary, highlight, accent);
        loop = [(0, tertiary), (1 / 3d, primary), (2 / 3d, secondary), (1, tertiary)];
        flare = [(0, White), (0.15, highlight), (0.5, secondary), (1, primary)];
    }

    /// <summary>Seamless tertiary → primary → secondary → tertiary gradient; any position wraps.</summary>
    public Rgb Loop(double at) => Ramp(Frac(at), loop);
    /// <summary>White-hot at 0 cooling through highlight and secondary to primary at 1.</summary>
    public Rgb Flare(double at) => Ramp(at, flare);

    public static LightPalette For(LightShowSettings show)
    {
        if (BoardLightShow.Themes.FirstOrDefault(t => t.Id == show.Theme) is { } theme)
            return new(Led(theme.Primary), Led(theme.Secondary), Led(theme.Tertiary), Led(theme.Highlight), Led(theme.Accent));
        var secondary = Led(show.Custom.Secondary);
        return new(Led(show.Custom.Primary), secondary, Led(show.Custom.Tertiary), Rgb.Mix(secondary, White, 0.5), Led(show.Custom.Accent));
    }

    /// <summary>A theme colour sets hue only: it is scaled to full level, then gamma-corrected.</summary>
    private static Rgb Led(string hex)
    {
        double red = Convert.ToInt32(hex.Substring(1, 2), 16), green = Convert.ToInt32(hex.Substring(3, 2), 16), blue = Convert.ToInt32(hex.Substring(5, 2), 16);
        var peak = Math.Max(red, Math.Max(green, blue));
        if (peak <= 0) return default;
        double Channel(double value) => 255 * Math.Pow(value / peak, Gamma);
        return new(Channel(red), Channel(green), Channel(blue));
    }
}

/// <summary>The board light show, one pure function per moment. Countdown, GO and game over keep the
/// BUSY Bar's beats (see <see cref="BusyBarAnimations"/>); play is deliberately calmer but never still,
/// because a frozen light is how an operator spots a crashed application.</summary>
internal static class BoardLightShow
{
    internal const string CustomTheme = "custom";
    internal static readonly IReadOnlyList<LightShowTheme> Themes =
    [
        new("neonSunset", "Neon sunset", "#ff4fad", "#ffad66", "#8c50ff", "#fff51f", "#96eddf"),
        new("aurora", "Aurora", "#1fd9a4", "#1f94d9", "#6a4cff", "#c9fff2", "#ff4fad"),
        new("ember", "Ember", "#ff4d1f", "#ffcc20", "#e01a8a", "#fff51f", "#7fe7ff")
    ];
    internal static bool KnownTheme(string? theme) => theme == CustomTheme || Themes.Any(t => t.Id == theme);
    /// <summary>Playing intensity as 0 (calm) to 1 (lively); null when unknown.</summary>
    internal static double? Level(string? intensity) => intensity switch { "calm" => 0, "balanced" => 0.5, "lively" => 1, _ => null };

    // Countdown: stage boundaries and the per-second pop match the BUSY Bar's countdown clip.
    private const double ScannerUntil = 0.6, PlayerEdgesUntil = 0.8, PopSeconds = 0.3;
    // GO: the burst reaches the row ends at BurstSeconds, then settles and blends into play.
    internal const double GoSeconds = BusyBarAnimations.GoSeconds, BlendSeconds = 0.75;
    private const double BurstSeconds = 0.3;
    // Play: peak level and motion at calm, plus what full intensity adds. The floors keep it moving.
    private const double PlayLevel = 0.35, PlayLevelRange = 0.20;
    private const double DriftLoopsPerSecond = 0.04, DriftRange = 0.05, DriftFloor = 0.02;
    private const double BreathDepth = 0.20, BreathDepthRange = 0.12, BreathSeconds = 9, BreathSecondsRange = 3;
    // Game over: the comet starts at the configured lap rate and coasts down to a quarter of it.
    private const double CometSlowdown = 0.75, GlowLevel = 0.22;
    internal const double TestPlaySeconds = 30, TestGoSeconds = 3;

    /// <summary>Where a pixel sits: X runs 0..Total along the row of boards, Around 0..1 clockwise round its board.</summary>
    internal readonly record struct Spot(double X, double Total, int Edge, int Board, double Around);

    internal static Rgb Countdown(LightPalette colors, Spot at, double elapsed, double preparation)
    {
        var progress = preparation > 0 ? Clamp01(elapsed / preparation) : 1;
        Rgb color;
        if (progress < ScannerUntil)
        {
            // A comet sweeps the row and returns once a second, so it leaves from the left on every count.
            var centre = (Math.Sin(2 * Math.PI * elapsed - Math.PI / 2) + 1) / 2 * at.Total;
            var glow = Math.Max(0, 1 - Math.Abs(at.X - centre) / Math.Max(0.35, 0.11 * at.Total));
            color = Rgb.Mix(colors.Primary, colors.Highlight, glow * glow) * glow;
        }
        else if (progress < PlayerEdgesUntil)
        {
            if (at.Edge is 1 or 3) color = default;
            else
            {
                double direction = at.Edge == 0 ? 1 : -1;
                var pulse = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (1.5 * at.X - direction * 3 * elapsed));
                color = Rgb.Mix(colors.Primary * 0.7, colors.Secondary, pulse * pulse);
            }
        }
        else
        {
            double direction = at.Board % 2 == 0 ? 1 : -1;
            color = colors.Loop(2 * at.Around - direction * 2 * elapsed);
        }
        // Every board pops white-hot on each second and harder as GO approaches: the 3-2-1 beat.
        var beat = Frac(elapsed + 1e-9);
        return Rgb.Mix(color, White, (0.6 + 0.4 * progress) * (1 - Clamp01(beat / PopSeconds)));
    }

    internal static Rgb Running(LightPalette colors, LightShowSettings show, Spot at, double elapsed)
    {
        if (elapsed < GoSeconds) return Go(colors, at, elapsed);
        var play = Playing(colors, show, at, elapsed);
        return elapsed < GoSeconds + BlendSeconds
            ? Rgb.Mix(colors.Accent * 0.5, play, Smooth((elapsed - GoSeconds) / BlendSeconds)) : play;
    }

    /// <summary>An accent flash with a white front racing from the centre of the row to both ends.</summary>
    internal static Rgb Go(LightPalette colors, Spot at, double elapsed)
    {
        var half = at.Total / 2;
        var distance = Math.Abs(at.X - half);
        var reach = half * (0.06 + elapsed / BurstSeconds);
        var flash = Math.Pow(Clamp01(1 - elapsed / BurstSeconds), 1.5);
        var fill = 1 - 0.5 * Smooth((elapsed - BurstSeconds) / (GoSeconds - BurstSeconds));
        var color = distance <= reach ? colors.Accent * fill : colors.Accent * (0.8 * flash) + White * (0.3 * Clamp01(1 - elapsed / 0.1));
        return Rgb.Mix(color, White, 0.9 * Clamp01(1 - Math.Abs(distance - reach) / Math.Max(0.12, 0.06 * at.Total)));
    }

    /// <summary>The theme gradient drifting along the row under a slow breathing wave. No channel exceeds
    /// the play level and nothing flashes; drift and breathing have floors so no setting can freeze it.</summary>
    internal static Rgb Playing(LightPalette colors, LightShowSettings show, Spot at, double elapsed)
    {
        var lively = Level(show.Intensity) ?? 0;
        var speed = show.Speed / 100d;
        var along = at.X / at.Total;
        var level = PlayLevel + PlayLevelRange * lively;
        var drift = Math.Max(DriftFloor, (DriftLoopsPerSecond + DriftRange * lively) * speed);
        var color = colors.Loop((0.5 + 0.25 * lively) * along - drift * elapsed);
        var period = Math.Clamp((BreathSeconds - BreathSecondsRange * lively) / speed, 4, 12);
        var breath = 1 - (BreathDepth + BreathDepthRange * lively) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (elapsed / period - 0.35 * along)));
        // Above calm, a soft accent swell crosses the row now and then.
        var swell = Math.Pow(Math.Max(0, Math.Cos(2 * Math.PI * (0.4 * along - 0.05 * speed * (0.5 + lively) * elapsed))), 16);
        return Rgb.Mix(color * (level * breath), colors.Accent * level, 0.6 * lively * swell);
    }

    /// <summary>Three weakening flares in the first part of the ending, each a ring expanding from the
    /// centre of the row, lit on the same sixths of <paramref name="flashSeconds"/> as the BUSY Bar.</summary>
    internal static Rgb Flare(LightPalette colors, Spot at, double elapsed, double flashSeconds)
    {
        var sixth = flashSeconds / 6;
        var interval = (int)(elapsed / sixth);
        var strength = 1 - 0.25 * (int)(elapsed / (sixth * 2));
        var ember = colors.Primary * (0.05 * strength);
        if (interval % 2 != 0) return ember;
        var local = elapsed - interval * sixth;
        var spread = Clamp01(local / sixth);
        var flare = Math.Pow(1 - spread, 1.5) * strength;
        var half = at.Total / 2;
        var distance = Math.Abs(at.X - half);
        var radius = half * (0.08 + 1.1 * spread);
        var color = ember + colors.Primary * (0.30 * flare);
        if (distance <= radius) color += colors.Flare(spread) * (0.45 * flare);
        color = Rgb.Mix(color, colors.Flare(spread), 0.9 * Clamp01(1 - Math.Abs(distance - radius) / Math.Max(0.15, 0.09 * at.Total)) * flare);
        return Rgb.Mix(color, White, 0.8 * Clamp01(1 - local / 0.08) * strength);
    }

    /// <summary>Glow left by the flares, cooling from primary to tertiary and sinking to black.</summary>
    internal static Rgb Afterglow(LightPalette colors, double since, double dotSeconds)
    {
        var progress = Clamp01(since / dotSeconds);
        return Rgb.Mix(colors.Primary, colors.Tertiary, progress) * (GlowLevel * Smooth(since / 0.3) * (1 - Smooth(progress)));
    }

    /// <summary>Head position and tail length of the comet, both as fractions of the dot path.</summary>
    internal static (double Head, double Tail) Comet(double since, AnimationSettings timing)
    {
        var progress = Clamp01(since / timing.DotSeconds);
        var laps = timing.DotLapsPerSecond * timing.DotSeconds * (progress - CometSlowdown / 2 * progress * progress);
        return (Frac(laps), Math.Clamp(0.15 * timing.DotLapsPerSecond * (1 - CometSlowdown * progress), 0.06, 0.5));
    }

    /// <summary>Colour of a comet pixel <paramref name="along"/> its tail (0 head, 1 tip) over the afterglow.</summary>
    internal static Rgb CometPixel(LightPalette colors, Rgb glow, double along, double since, double dotSeconds)
    {
        var strength = (1 - along) * (1 - along) * (1 - Smooth((Clamp01(since / dotSeconds) - 0.8) / 0.2));
        return Rgb.Mix(glow, colors.Flare(along), strength);
    }

    /// <summary>Phase and phase time a test segment shows after <paramref name="elapsed"/> seconds; null once it is over.</summary>
    internal static (GamePhase Phase, double Elapsed)? TestScene(string segment, double elapsed, AnimationSettings timing)
    {
        var preparation = CountdownService.PreCountdownDuration.TotalSeconds;
        return segment switch
        {
            "countdown" when elapsed < preparation => (GamePhase.PreCountdown, elapsed),
            "countdown" when elapsed < preparation + TestGoSeconds => (GamePhase.Running, elapsed - preparation),
            "playing" when elapsed < TestPlaySeconds => (GamePhase.Running, elapsed + GoSeconds + BlendSeconds),
            "gameOver" when elapsed < timing.FlashSeconds + timing.DotSeconds => (GamePhase.Ending, elapsed),
            _ => null
        };
    }

    /// <summary>Length of a test segment in seconds; null for an unknown segment.</summary>
    internal static double? TestSeconds(string segment, AnimationSettings timing) => segment switch
    {
        "countdown" => CountdownService.PreCountdownDuration.TotalSeconds + TestGoSeconds,
        "playing" => TestPlaySeconds,
        "gameOver" => timing.FlashSeconds + timing.DotSeconds,
        _ => null
    };
}
