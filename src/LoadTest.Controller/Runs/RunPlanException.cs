namespace LoadTest.Controller.Runs;

/// <summary>La run non può partire: tutti i motivi raccolti (load test non valido, agenti mancanti, file non distribuibili, ...).</summary>
public sealed class RunPlanException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
