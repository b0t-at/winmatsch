using WinMatsch.Core;

namespace WinMatsch.Analysis.Dependencies;

/// <summary>The runtime family described by payload evidence.</summary>
public enum DependencyEvidenceKind
{
    VisualCppRuntime,
    DotNetRuntime,
}
/// <summary>
/// How strongly payload metadata supports a runtime dependency. These values describe evidence,
/// not policy: callers must not treat <see cref="Inferred"/> or <see cref="Ambiguous"/> as a
/// mandatory dependency without additional confirmation.
/// </summary>
public enum DependencyEvidenceStatus
{
    Detected,
    Inferred,
    Ambiguous,
    Absent,
    /// <summary>
    /// The evidence source was not inspected completely because a configured resource budget,
    /// stream capability, or supported-format boundary was reached.
    /// </summary>
    Unavailable,
}

/// <summary>
/// One runtime-evidence result associated with the installer or nested payload that produced it.
/// </summary>
public sealed class DependencyEvidence
{
    private IReadOnlyList<string> _signals = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// Installer file name or normalized archive-relative payload path that produced the evidence.
    /// </summary>
    public required string PayloadPath { get; init; }

    /// <summary>The payload's PE architecture, or null when no PE could be associated safely.</summary>
    public Architecture? Architecture { get; init; }

    /// <summary>The runtime family examined.</summary>
    public required DependencyEvidenceKind Kind { get; init; }

    /// <summary>The certainty of this evidence.</summary>
    public required DependencyEvidenceStatus Status { get; init; }

    /// <summary>The .NET runtime major when metadata identifies one; null for VC++ evidence.</summary>
    public int? RuntimeMajor { get; init; }

    /// <summary>Normalized metadata signals supporting the status.</summary>
    public IReadOnlyList<string> Signals
    {
        get => _signals;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _signals = Array.AsReadOnly(value.ToArray());
        }
    }
}

/// <summary>Bounded runtime-dependency evidence collected from one installer payload.</summary>
public sealed class PayloadDependencyAnalysis
{
    private readonly IReadOnlyList<DependencyEvidence> _evidence;
    private readonly IReadOnlyList<AnalysisDiagnostic> _diagnostics;
    private readonly bool _isComplete;

    public PayloadDependencyAnalysis(IReadOnlyList<DependencyEvidence> evidence)
        : this(evidence, [], isComplete: true)
    {
    }

    public PayloadDependencyAnalysis(
        IReadOnlyList<DependencyEvidence> evidence,
        IReadOnlyList<AnalysisDiagnostic> diagnostics,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _evidence = Array.AsReadOnly(evidence.ToArray());
        _diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        _isComplete = isComplete;
    }

    public IReadOnlyList<DependencyEvidence> Evidence => _evidence;

    /// <summary>Non-fatal reasons why dependency evidence is incomplete or needs review.</summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Whether every relevant payload was inspected within the configured bounds.</summary>
    public bool IsComplete => _isComplete
        && !_evidence.Any(static item => item.Status == DependencyEvidenceStatus.Unavailable);
}
