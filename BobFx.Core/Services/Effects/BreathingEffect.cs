using System.Numerics;

namespace BobFx.Core.Services.Effects;

public sealed class BreathingEffect() : RgbEffectBase(new Vector3(1, 0, 0))
{
    private static readonly RgbEffectInfo Info = new()
    {
        RequiredColors = 1,
        Description = "Breathing effect that fades between configurable minimum and maximum brightness",
        DefaultSpeed = TimeSpan.FromMilliseconds(50),
        ColorLabels = ["Color"],
        CustomParameters = new Dictionary<string, string> { ["BreathDuration"] = "duration", ["MinBrightness"] = "number", ["MaxBrightness"] = "number" }
    };
    public override RgbEffect EffectType => RgbEffect.Breathing;
    public Vector3 Color { get => Palette[0]; set => Palette[0] = value; }
    /// <summary>The time from darkest to brightest; a whole breath takes twice as long.</summary>
    public TimeSpan BreathDuration { get; set; } = TimeSpan.FromSeconds(2);
    public float MinBrightness { get; set; } = 0f;
    public float MaxBrightness { get; set; } = 1f;
    public override RgbEffectInfo GetInfo() => Info;

    public override bool Apply(Span<Vector3> leds)
    {
        var breath = BreathDuration.TotalMilliseconds * 2;
        var progress = Clock.GetElapsedTime(StartTimestamp).TotalMilliseconds % breath / breath;
        // Up for the first half of a breath and down for the second.
        var level = (float)(progress < 0.5 ? progress * 2 : (1 - progress) * 2);
        leds.Fill(Color * Math.Clamp(MinBrightness + level * (MaxBrightness - MinBrightness), MinBrightness, MaxBrightness));
        return false;
    }

    protected override IEnumerable<string> Problems()
    {
        if (BreathDuration <= TimeSpan.Zero) yield return "Breath duration must be greater than zero";
        if (!IsUnit(MinBrightness) || !IsUnit(MaxBrightness)) yield return "Brightness must be finite and between 0 and 1";
        else if (MinBrightness >= MaxBrightness) yield return "Minimum brightness must be less than maximum brightness";
    }
}
