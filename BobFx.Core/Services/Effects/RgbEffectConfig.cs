namespace BobFx.Core.Services.Effects;

/// <summary>
/// Metadata about what each effect needs.
/// </summary>
public class RgbEffectInfo
{
    public int RequiredColors { get; set; }
    public int OptionalColors { get; set; }
    public string Description { get; set; } = string.Empty;
    public TimeSpan DefaultSpeed { get; set; }
    public string[] ColorLabels { get; set; } = [];
    public Dictionary<string, string> CustomParameters { get; set; } = [];

    public int TotalColors => RequiredColors + OptionalColors;
}
