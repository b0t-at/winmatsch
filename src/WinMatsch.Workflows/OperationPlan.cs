using WinMatsch.Validation;

namespace WinMatsch.Workflows;

/// <summary>A workflow's complete dry-run result before any mutation is applied.</summary>
public sealed class OperationPlan
{
    private readonly IReadOnlyList<PlannedChange> _changes;

    public OperationPlan(
        string operation,
        IEnumerable<PlannedChange>? changes = null,
        ValidationReport? validation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
        _changes = changes is null ? [] : [.. changes];
        Validation = validation ?? new ValidationReport();
    }

    public string Operation { get; }

    public IReadOnlyList<PlannedChange> Changes => _changes;

    public ValidationReport Validation { get; }

    public bool CanApply => Changes.Count > 0 && Validation.IsValid;
}
