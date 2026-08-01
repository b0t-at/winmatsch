using System.Text.RegularExpressions;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// DEP-2: classifies supplied validation-pipeline log lines that match the known
/// dependency-index outage signature ("No suitable installer found for manifest
/// Microsoft.VCRedist.2015+.&lt;arch&gt; / Microsoft.DotNet.[Desktop]Runtime.&lt;major&gt; with
/// version …" — the winget-pkgs VCRedist index outage, issue #152555) as
/// <em>infrastructure</em> findings. Only the exact well-known runtime dependency package
/// shapes match, so a typo'd or genuinely wrong dependency in the manifest is never waved
/// through as infra. The manifest is never mutated in response: chasing an infra error by
/// editing the manifest is exactly the failure mode this rule exists to prevent. Keeping the
/// PR alive / re-running validation is the workflow layer's job; this rule only supplies the
/// deterministic classification.
/// </summary>
public sealed partial class Dep2DependencyOutageRule : IRule
{
    public string Id => RuleCatalogueIds.Dep2;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Classifies dependency-outage pipeline signatures as infrastructure, never as manifest errors.";

    private readonly PolicyEvidence _evidence;

    public Dep2DependencyOutageRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (string line in _evidence.PipelineLogExcerpts)
        {
            Match match = OutageSignature().Match(line);
            if (!match.Success)
            {
                continue;
            }

            context.AddFinding(this, RuleSeverity.Info,
                $"Pipeline failure '{match.Value}' matches a known dependency-index outage signature. This is infrastructure, not a manifest error: do not mutate the manifest; re-run validation instead.");
        }
    }

    [GeneratedRegex(@"No suitable installer found for manifest\s+Microsoft\.(VCRedist\.2015\+\.(x86|x64|arm64)|DotNet\.(Desktop)?Runtime\.\d+)\s+with version\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex OutageSignature();
}
