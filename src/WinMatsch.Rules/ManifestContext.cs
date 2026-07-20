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

    /// <summary>The manifests being produced. Normalization and quirk rules mutate these in place.</summary>
    public required PackageManifests Manifests { get; init; }

    /// <summary>The previous version's manifests when this run updates an existing package; null for new packages.</summary>
    public PackageManifests? Previous { get; init; }

    /// <summary>Analysis evidence per installer URL; empty when installers were not downloaded.</summary>
    public IReadOnlyList<InstallerEvidence> Evidence { get; init; } = [];

    public RuleOptions Options { get; init; } = new();

    /// <summary>The findings collected so far, in the deterministic order they were added.</summary>
    public IReadOnlyList<RuleFinding> Findings => _findings;

    /// <summary>The explain log; only populated when <see cref="RuleOptions.Explain"/> is set.</summary>
    public IReadOnlyList<RuleTraceEntry> Trace => _trace;

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
        _findings.Add(new RuleFinding(rule.Id, severity, message, path));
        AddTrace(rule, path is null ? message : $"{path}: {message}");
    }

    /// <summary>Records what a rule changed or found; no-op unless <see cref="RuleOptions.Explain"/> is set.</summary>
    public void AddTrace(IRule rule, string message)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(message);
        if (Options.Explain)
        {
            _trace.Add(new RuleTraceEntry(rule.Id, message));
        }
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
}
