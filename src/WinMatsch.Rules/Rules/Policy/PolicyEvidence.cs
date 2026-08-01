using System.Collections.ObjectModel;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// Pre-computed, externally supplied evidence the deterministic policy rules consume. Policy
/// rules never perform network or file-system probes themselves: live checks (HTTPS probes,
/// HEAD requests, repository index scans, pipeline log capture) are owned by
/// WinMatsch.Validation / WinMatsch.Workflows, which run them ahead of the pipeline and hand
/// the results in through this model. A missing piece of evidence always means "do not mutate"
/// — rules either skip or emit an explicit finding naming the evidence they would need.
/// All lookups are deterministic; URL keys compare case-insensitively (ordinal).
/// </summary>
public sealed class PolicyEvidence
{
    /// <summary>An empty evidence set: every rule falls back to its no-evidence behavior.</summary>
    public static PolicyEvidence Empty { get; } = new();

    /// <summary>
    /// META-1: the exact <c>http://</c> URLs whose <c>https://</c> variant a workflow probe
    /// confirmed reachable (2xx/3xx). Only listed URLs are upgraded.
    /// </summary>
    public IReadOnlyCollection<string> HttpsUpgradeConfirmations { get; init; } = [];

    /// <summary>
    /// META-4 / META-5: URLs a workflow probe confirmed reachable. Used to validate a
    /// version-swapped ReleaseNotesUrl and to carry previous URL fields forward.
    /// </summary>
    public IReadOnlyCollection<string> ConfirmedUrls { get; init; } = [];

    /// <summary>
    /// ARP-2: ARP DisplayVersion values already declared by <em>other</em> versions of this
    /// package in the target repository (supplied by a workflow-side index scan). Without this
    /// set the rule performs no overlap check — it never guesses at index contents.
    /// </summary>
    public IReadOnlyCollection<string> ExistingDisplayVersions { get; init; } = [];

    /// <summary>
    /// DEP-1: payload runtime-dependency evidence per installer URL, produced by
    /// <c>PayloadDependencyAnalyzer</c> ahead of the pipeline run.
    /// </summary>
    public IReadOnlyDictionary<string, PayloadDependencyAnalysis> DependencyAnalyses { get; init; }
        = EmptyDictionary<PayloadDependencyAnalysis>();

    /// <summary>
    /// SCOPE-2: explicit installation-scope evidence per installer URL. Only evidence whose
    /// <see cref="PolicyScopeEvidence.Origin"/> is direct installer metadata is trusted.
    /// </summary>
    public IReadOnlyDictionary<string, PolicyScopeEvidence> InstallerScopes { get; init; }
        = EmptyDictionary<PolicyScopeEvidence>();

    /// <summary>
    /// PIPE-4: installer URLs whose portable payload was determined (by archive import
    /// analysis) to load DLLs that sit next to it inside the archive.
    /// </summary>
    public IReadOnlyCollection<string> SiblingImportUrls { get; init; } = [];

    /// <summary>
    /// DEP-2: raw validation-pipeline log lines supplied by the workflow for classification.
    /// </summary>
    public IReadOnlyList<string> PipelineLogExcerpts { get; init; } = [];

    /// <summary>
    /// PIPE-2: the raw <c>$schema</c> header comment observed per manifest file path, when the
    /// caller read the emitted files. Raw header regeneration/validation stays in
    /// WinMatsch.Validation; this rule only cross-checks supplied observations.
    /// </summary>
    public IReadOnlyDictionary<string, string> SchemaHeaderComments { get; init; }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// META-5: the release date of the new version as supplied by release metadata, used to
    /// recompute (never copy) a <c>ReleaseDate</c> the previous manifest declared.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>Case-insensitive membership test against <see cref="HttpsUpgradeConfirmations"/>.</summary>
    internal bool IsHttpsUpgradeConfirmed(string httpUrl)
        => ContainsIgnoreCase(HttpsUpgradeConfirmations, httpUrl);

    /// <summary>Case-insensitive membership test against <see cref="ConfirmedUrls"/>.</summary>
    internal bool IsUrlConfirmed(string url) => ContainsIgnoreCase(ConfirmedUrls, url);

    /// <summary>Case-insensitive membership test against <see cref="SiblingImportUrls"/>.</summary>
    internal bool HasSiblingImports(string installerUrl)
        => ContainsIgnoreCase(SiblingImportUrls, installerUrl);

    /// <summary>Case-insensitive lookup in <see cref="DependencyAnalyses"/>.</summary>
    internal PayloadDependencyAnalysis? FindDependencyAnalysis(string? installerUrl)
        => FindIgnoreCase(DependencyAnalyses, installerUrl);

    /// <summary>Case-insensitive lookup in <see cref="InstallerScopes"/>.</summary>
    internal PolicyScopeEvidence? FindScopeEvidence(string? installerUrl)
        => FindIgnoreCase(InstallerScopes, installerUrl);

    private static bool ContainsIgnoreCase(IReadOnlyCollection<string> values, string value)
    {
        foreach (string candidate in values)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindIgnoreCase<T>(IReadOnlyDictionary<string, T> values, string? key)
        where T : class
    {
        if (key is null)
        {
            return null;
        }

        if (values.TryGetValue(key, out T? direct))
        {
            return direct;
        }

        foreach ((string candidate, T value) in values)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static ReadOnlyDictionary<string, T> EmptyDictionary<T>()
        => new(new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Where a piece of scope evidence was read from; only direct installer metadata is trusted.</summary>
public enum PolicyScopeEvidenceOrigin
{
    /// <summary>The MSI <c>ALLUSERS</c> property (or per-user equivalent) read from the package itself.</summary>
    MsiAllUsersProperty,

    /// <summary>The Inno Setup <c>PrivilegesRequired</c> directive read from the installer header.</summary>
    InnoPrivilegesRequired,

    /// <summary>
    /// Metadata of a generic wrapper (outer stub, download page, embedded resource strings).
    /// SCOPE-2 never derives a scope from this origin.
    /// </summary>
    WrapperMetadata,
}

/// <summary>Explicit installation-scope evidence for one installer URL (SCOPE-2 input).</summary>
public sealed record PolicyScopeEvidence
{
    /// <summary>The scope the evidence supports.</summary>
    public required Scope Scope { get; init; }

    /// <summary>Where the evidence was read from; wrapper metadata is never trusted.</summary>
    public required PolicyScopeEvidenceOrigin Origin { get; init; }

    /// <summary>Human-readable provenance, e.g. <c>MSI ALLUSERS=1</c>.</summary>
    public required string Source { get; init; }
}
