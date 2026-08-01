using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// PIPE-5: applies the content-policy annotations maintained in the package's override pack —
/// the proactive traps list (blocked script installers, publishers that block validation-host
/// networks, Defender false-positive risks, service installers needing elevation, and
/// manual-only packages). Every annotation produces a deterministic finding; the single
/// mutation is setting <c>ElevationRequirement: elevationRequired</c> for an explicit
/// <c>needs-elevation</c> annotation. Unknown annotation ids are surfaced rather than ignored.
/// </summary>
public sealed class Pipe5ContentPolicyAnnotationRule : IRule
{
    public const string BlockedInstallerType = "blocked-installer-type";
    public const string ManualOnly = "manual-only";
    public const string NetworkBlocked = "network-blocked-publishers";
    public const string DefenderFalsePositiveRisk = "defender-fp-risk";
    public const string NeedsElevation = "needs-elevation";

    private readonly OverridePackSet _overridePacks;

    public Pipe5ContentPolicyAnnotationRule(OverridePackSet? overridePacks = null)
    {
        _overridePacks = overridePacks ?? OverridePackSet.Empty;
    }

    public string Id => RuleCatalogueIds.Pipe5;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Applies override-pack content-policy annotations (blocked types, network blocks, Defender risk, elevation).";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? pack)
            || pack is null)
        {
            return;
        }

        if (pack.ManualOnly)
        {
            context.AddFinding(this, RuleSeverity.Warning,
                "This package is marked manual-only in its override pack; automated submission must not proceed.");
        }

        foreach (PolicyAnnotation annotation in pack.Policies)
        {
            switch (annotation.Id.ToLowerInvariant())
            {
                case BlockedInstallerType:
                    context.AddFinding(this, RuleSeverity.Error,
                        $"Content policy: the installer type is blocked by repository policy ({annotation.Annotation}); the submission would be rejected as a scripted/blocked application.");
                    break;
                case ManualOnly:
                    context.AddFinding(this, RuleSeverity.Warning,
                        $"Content policy: package is annotated manual-only ({annotation.Annotation}); automated submission must not proceed.");
                    break;
                case NetworkBlocked:
                    context.AddFinding(this, RuleSeverity.Info,
                        $"Content policy: the publisher blocks validation-host networks ({annotation.Annotation}); expect manual validation and do not auto-abandon the submission.");
                    break;
                case DefenderFalsePositiveRisk:
                    context.AddFinding(this, RuleSeverity.Warning,
                        $"Content policy: known Defender/SmartScreen false-positive risk ({annotation.Annotation}); expect the false-positive workflow rather than a manifest defect.");
                    break;
                case NeedsElevation:
                    ApplyElevationRequirement(context, annotation);
                    break;
                default:
                    if (!string.Equals(annotation.Id, RuleCatalogueIds.Arp4, StringComparison.OrdinalIgnoreCase))
                    {
                        context.AddFinding(this, RuleSeverity.Info,
                            $"Override pack carries an annotation with unrecognized id '{annotation.Id}' ({annotation.Annotation}); no automated behavior is attached to it.");
                    }

                    break;
            }
        }
    }

    private void ApplyElevationRequirement(ManifestContext context, PolicyAnnotation annotation)
    {
        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            ElevationRequirement? effective = installer.ElevationRequirement ?? manifest.ElevationRequirement;
            if (effective is { } existing)
            {
                if (existing != ElevationRequirement.ElevationRequired)
                {
                    context.AddFinding(this, RuleSeverity.Warning,
                        $"Override pack requires elevation ({annotation.Annotation}) but the entry declares '{existing}'; review required — not changed.",
                        $"Installers[{i}]");
                }

                continue;
            }

            installer.ElevationRequirement = ElevationRequirement.ElevationRequired;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"Installers[{i}].ElevationRequirement",
                $"override-pack needs-elevation annotation: {annotation.Annotation}",
                RuleChangeConfidence.High);
            context.AddTrace(this, $"Installers[{i}]: set ElevationRequirement=elevationRequired from the override pack.");
        }
    }
}
