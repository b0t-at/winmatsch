namespace WinMatsch.Validation.Schema;

internal sealed record SchemaEvaluationError(
    string InstanceLocation,
    string Keyword,
    string Message);

internal sealed class Draft7EvaluationResult
{
    internal Draft7EvaluationResult(IEnumerable<SchemaEvaluationError> errors)
    {
        Errors = [.. errors];
    }

    internal IReadOnlyList<SchemaEvaluationError> Errors { get; }

    internal bool IsValid => Errors.Count == 0;
}
