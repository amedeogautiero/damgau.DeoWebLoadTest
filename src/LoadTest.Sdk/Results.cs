namespace LoadTest.Sdk;

public sealed record ExtractionResult(bool Success, string? Value = null, string? Message = null)
{
    public static ExtractionResult Ok(string value) => new(true, value);
    public static ExtractionResult Fail(string message) => new(false, null, message);
}

public sealed record ValidationResult(bool Passed, string? Message = null)
{
    public static ValidationResult Ok() => new(true);
    public static ValidationResult Fail(string message) => new(false, message);
}
