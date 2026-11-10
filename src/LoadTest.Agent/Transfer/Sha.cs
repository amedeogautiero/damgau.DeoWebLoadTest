namespace LoadTest.Agent.Transfer;

public static class Sha
{
    public static bool IsValid(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
}
