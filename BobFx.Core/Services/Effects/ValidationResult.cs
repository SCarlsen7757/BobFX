namespace BobFx.Core.Services.Effects;

/// <summary>
/// Represents the result of validating an effect configuration.
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = [];

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult Fail(params string[] errors) =>
        new() { IsValid = false, Errors = [.. errors] };

    public void AddError(string error)
    {
        IsValid = false;
        Errors.Add(error);
    }

    public override string ToString() =>
        IsValid ? "Valid" : $"Invalid: {string.Join(", ", Errors)}";
}
