namespace WinMatsch.Validation;

/// <summary>A stable, machine-readable validation diagnostic.</summary>
public sealed record ValidationFinding(
    string Code,
    ValidationSeverity Severity,
    string Message,
    string? Path = null);
