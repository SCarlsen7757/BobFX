namespace BobFx.Core.Services.Effects;

/// <summary>What an effect is and takes, for the page that lets an operator start it. One instance per effect class.</summary>
public sealed record RgbEffectInfo
{
    public int RequiredColors { get; init; }
    public int OptionalColors { get; init; }
    public string Description { get; init; } = string.Empty;
    public TimeSpan DefaultSpeed { get; init; }
    /// <summary>A label per color, required ones first.</summary>
    public string[] ColorLabels { get; init; } = [];
    /// <summary>Further parameters the effect takes, by name, with the kind of value each is.</summary>
    public IReadOnlyDictionary<string, string> CustomParameters { get; init; } = new Dictionary<string, string>();

    public int TotalColors => RequiredColors + OptionalColors;
}
