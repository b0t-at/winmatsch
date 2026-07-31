using System.Collections.ObjectModel;
using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// Everything a rule may look at or mutate during one pipeline run: the manifests being
/// produced, the previous version's manifests (when updating), per-installer analysis
/// evidence, options, plus the findings and explain-trace collected along the way.
/// </summary>
public sealed class ManifestContext
{
    private readonly List<RuleFinding> _findings = [];
    private readonly List<RuleTraceEntry> _trace = [];
    private readonly List<RuleChange> _changes = [];
    private readonly List<RuleExecution> _executions = [];
    private readonly List<HumanCorrectionReview> _humanCorrectionReviews = [];
    private readonly Dictionary<string, RuleChangeEvidence> _changeEvidence = new(StringComparer.Ordinal);
    private readonly ReadOnlyCollection<RuleFinding> _findingsView;
    private readonly ReadOnlyCollection<RuleTraceEntry> _traceView;
    private readonly ReadOnlyCollection<RuleChange> _changesView;
    private readonly ReadOnlyCollection<RuleExecution> _executionsView;
    private readonly ReadOnlyCollection<HumanCorrectionReview> _humanCorrectionReviewsView;

    public ManifestContext()
    {
        _findingsView = _findings.AsReadOnly();
        _traceView = _trace.AsReadOnly();
        _changesView = _changes.AsReadOnly();
        _executionsView = _executions.AsReadOnly();
        _humanCorrectionReviewsView = _humanCorrectionReviews.AsReadOnly();
    }

    /// <summary>The manifests being produced. Normalization and quirk rules mutate these in place.</summary>
    public required PackageManifests Manifests { get; init; }

    /// <summary>The previous version's manifests when this run updates an existing package; null for new packages.</summary>
    public PackageManifests? Previous { get; init; }

    /// <summary>
    /// The exact manifest set originally proposed by automation for the version represented by
    /// <see cref="Previous"/>. Supplying both enables three-way human-correction detection.
    /// </summary>
    public PackageManifests? OriginalBotSubmission { get; init; }

    /// <summary>Analysis evidence per installer URL; empty when installers were not downloaded.</summary>
    public IReadOnlyList<InstallerEvidence> Evidence { get; init; } = [];

    public RuleOptions Options { get; init; } = new();

    /// <summary>The findings collected so far, in the deterministic order they were added.</summary>
    public IReadOnlyList<RuleFinding> Findings => _findingsView;

    /// <summary>The explain log; only populated when <see cref="RuleOptions.Explain"/> is set.</summary>
    public IReadOnlyList<RuleTraceEntry> Trace => _traceView;

    /// <summary>Applied changes and log-only proposals, in deterministic execution order.</summary>
    public IReadOnlyList<RuleChange> Changes => _changesView;

    /// <summary>The effective mode selected for every rule in pipeline order.</summary>
    public IReadOnlyList<RuleExecution> Executions => _executionsView;

    /// <summary>Known human corrections that generated output would revert.</summary>
    public IReadOnlyList<HumanCorrectionReview> HumanCorrectionReviews => _humanCorrectionReviewsView;

    /// <summary>True when a known human correction would be reverted and review is required.</summary>
    public bool RequiresReview => _humanCorrectionReviews.Count != 0;

    /// <summary>Adds a finding with the rule's default severity and mirrors it into the explain trace.</summary>
    public void AddFinding(IRule rule, string message, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        AddFinding(rule, rule.Severity, message, path);
    }

    /// <summary>Adds a finding with an explicit severity and mirrors it into the explain trace.</summary>
    public void AddFinding(IRule rule, RuleSeverity severity, string message, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(message);
        string safeMessage = RuleLogSanitizer.SanitizeMessage(message);
        string? safePath = path is null ? null : RuleLogSanitizer.SanitizeMessage(path);
        _findings.Add(new RuleFinding(rule.Id, severity, safeMessage, safePath));
        AddTrace(rule, safePath is null ? safeMessage : $"{safePath}: {safeMessage}");
    }

    /// <summary>Records what a rule changed or found; no-op unless <see cref="RuleOptions.Explain"/> is set.</summary>
    public void AddTrace(IRule rule, string message)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(message);
        if (Options.Explain)
        {
            _trace.Add(new RuleTraceEntry(rule.Id, RuleLogSanitizer.SanitizeMessage(message)));
        }
    }

    /// <summary>
    /// Attaches source evidence to a manifest field that a rule changed. Paths use the same
    /// canonical form as <see cref="RuleChange.FieldPath"/>.
    /// </summary>
    public void AddChangeEvidence(
        IRule rule,
        string manifestPath,
        string fieldPath,
        string source,
        RuleChangeConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _changeEvidence[EvidenceKey(rule.Id, manifestPath, fieldPath)] = new(source, confidence);
    }

    /// <summary>Finds the evidence for an installer URL (case-insensitive), or null when there is none.</summary>
    public InstallerEvidence? FindEvidence(string? installerUrl)
    {
        if (installerUrl is null)
        {
            return null;
        }

        foreach (InstallerEvidence evidence in Evidence)
        {
            if (string.Equals(evidence.InstallerUrl, installerUrl, StringComparison.OrdinalIgnoreCase))
            {
                return evidence;
            }
        }

        return null;
    }

    internal RuleChangeEvidence? FindChangeEvidence(string ruleId, string manifestPath, string fieldPath)
        => _changeEvidence.GetValueOrDefault(EvidenceKey(ruleId, manifestPath, fieldPath));

    internal void AddChange(RuleChange change) => _changes.Add(change);

    internal void AddExecution(RuleExecution execution) => _executions.Add(execution);

    internal void AddHumanCorrectionReview(HumanCorrectionReview review) => _humanCorrectionReviews.Add(review);

    internal void ImportFindings(IEnumerable<RuleFinding> findings) => _findings.AddRange(findings);

    internal void ImportTrace(IEnumerable<RuleTraceEntry> trace)
    {
        if (Options.Explain)
        {
            _trace.AddRange(trace);
        }
    }

    internal static string GetInstallerManifestPath(PackageManifests manifests)
        => ManifestSnapshot.GetInstallerPath(manifests);

    private static string EvidenceKey(string ruleId, string manifestPath, string fieldPath)
        => $"{ruleId}\u001f{manifestPath}\u001f{fieldPath}";
}
