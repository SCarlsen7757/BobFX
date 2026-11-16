using System.Numerics;

namespace BobFx.Core.Services.Effects;

/// <summary>A color that fades over a set time and then stops: in from black, or out to black.</summary>
public abstract class FadeEffect(bool fadeIn) : RgbEffectBase(new Vector3(1, 0, 0))
{
    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromSeconds(2);

    public override bool Apply(Span<Vector3> leds)
    {
        var elapsed = Clock.GetElapsedTime(StartTimestamp);
        var done = elapsed >= FadeDuration;
        var progress = done ? 1f : Math.Clamp((float)(elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds), 0f, 1f);
        leds.Fill(Palette[0] * (fadeIn ? progress : 1f - progress));
        return done;
    }

    protected override IEnumerable<string> Problems()
    {
        if (FadeDuration <= TimeSpan.Zero) yield return "Fade duration must be greater than zero";
    }

    protected static RgbEffectInfo Describe(string description, string colorLabel) => new()
    {
        RequiredColors = 1,
        Description = description,
        DefaultSpeed = TimeSpan.FromMilliseconds(50),
        ColorLabels = [colorLabel],
        CustomParameters = new Dictionary<string, string> { ["FadeDuration"] = "duration" }
    };
}
