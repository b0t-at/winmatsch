namespace WinMatsch.Workflows;

/// <summary>The kind of filesystem or repository change in an operation plan.</summary>
public enum PlannedChangeKind
{
    Add,
    Update,
    Delete,
}

/// <summary>One deterministic change proposed by a workflow.</summary>
public sealed record PlannedChange(
    PlannedChangeKind Kind,
    string Path,
    string Description);
