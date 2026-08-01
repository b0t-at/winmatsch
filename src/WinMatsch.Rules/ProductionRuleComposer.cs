using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;

namespace WinMatsch.Rules;

/// <summary>
/// Composes the complete production rule catalogue in deterministic execution order.
/// Runtime modes are resolved by <see cref="RulePipeline"/> for each invocation.
/// </summary>
public static class ProductionRuleComposer
{
    public static IReadOnlyList<IRule> Compose(
        PolicyEvidence? evidence = null,
        OverridePackSet? overridePacks = null)
    {
        PolicyEvidence runEvidence = evidence ?? PolicyEvidence.Empty;
        OverridePackSet packs = overridePacks ?? OverridePackSet.BuiltIn;
        return
        [
            // Preservation and package-specific behavior must see the original update shape.
            new PreserveOnUpdateRule(),
            new ApplyPackageQuirksRule(packs),

            // Generic normalization precedes policy so policy observes canonical manifests.
            new PushDownRootFieldsRule(),
            new ScrubEmptyStringsRule(),
            new NormalizeProductCodesRule(),
            new DedupeArpVsDefaultLocaleRule(),
            new RemoveDuplicateInstallersRule(),
            new HoistCommonInstallerFieldsRule(),

            // Mutating policy rules. ARP-1 must template before ARP-2 removes redundancy.
            new Arp1VersionTemplateRule(),
            new Arp2DisplayVersionRedundancyRule(runEvidence),
            new Arp3SanitizeInstallerValuesRule(),
            new Scope1UserMachineTwinRule(),
            new Scope2ExplicitScopeFromEvidenceRule(runEvidence),
            new Scope3SwitchHygieneRule(),
            new Scope4WrapperClassificationRule(),
            new Meta5FieldSetParityRule(runEvidence, packs),
            new Meta1HttpsUpgradeRule(runEvidence),
            new Meta3GitHubLicenseUrlRule(),
            new Meta4ReleaseNotesSanitizeRule(runEvidence),
            new Dep1PayloadDependencyRule(runEvidence),
            new Pipe2ManifestVersionPinRule(runEvidence),
            new Pipe4ArchiveBinariesDependOnPathRule(runEvidence),
            new Pipe5ContentPolicyAnnotationRule(packs),

            // Finding-only policy rules run after every mutation; PIPE-1 is the final policy guard.
            new Arp4ShapeParityRule(packs),
            new Dep2DependencyOutageRule(runEvidence),
            new Pipe3IdentityImmutabilityRule(),
            new Pipe1SerializerInvariantsRule(),

            // WM01xx validation always observes the final manifest state.
            new DisplayVersionConsistencyRule(),
            new DuplicateInstallerEntriesRule(),
            new InstallerTypeConsistencyRule(),
        ];
    }
}
