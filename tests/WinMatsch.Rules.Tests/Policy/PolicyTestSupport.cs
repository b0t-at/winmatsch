using WinMatsch.Core;
using WinMatsch.Rules.Policy;

namespace WinMatsch.Rules.Tests.Policy;

/// <summary>Shared helpers for the policy catalogue rule tests.</summary>
internal static class PolicyTestSupport
{
    /// <summary>Runs a single rule through the pipeline in the given runtime mode.</summary>
    public static ManifestContext RunViaPipeline(
        IRule rule,
        PackageManifests manifests,
        RuleMode mode,
        PackageManifests? previous = null,
        IReadOnlyList<InstallerEvidence>? evidence = null)
    {
        var context = new ManifestContext
        {
            Manifests = manifests,
            Previous = previous,
            Evidence = evidence ?? [],
            Options = new RuleOptions { Explain = true },
        };
        var runtime = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode> { [rule.Id] = mode });
        RulePipeline.Create([rule], runtime).Run(context);
        return context;
    }

    /// <summary>A previous-version manifest set with its own version string.</summary>
    public static PackageManifests CreatePrevious(string version, params Installer[] installers)
    {
        PackageManifests manifests = TestManifests.Create(installers);
        var packageVersion = new PackageVersion(version);
        manifests.Installer.PackageVersion = packageVersion;
        manifests.DefaultLocale.PackageVersion = packageVersion;
        manifests.Version.PackageVersion = packageVersion;
        return manifests;
    }

    /// <summary>Every policy catalogue rule with default (empty) evidence and overrides.</summary>
    public static IReadOnlyList<IRule> CreateAllPolicyRules() =>
    [
        new Arp1VersionTemplateRule(),
        new Arp2DisplayVersionRedundancyRule(),
        new Arp3SanitizeInstallerValuesRule(),
        new Arp4ShapeParityRule(),
        new Scope1UserMachineTwinRule(),
        new Scope2ExplicitScopeFromEvidenceRule(),
        new Scope3SwitchHygieneRule(),
        new Scope4WrapperClassificationRule(),
        new Meta1HttpsUpgradeRule(),
        new Meta3GitHubLicenseUrlRule(),
        new Meta4ReleaseNotesSanitizeRule(),
        new Meta5FieldSetParityRule(),
        new Dep1PayloadDependencyRule(),
        new Dep2DependencyOutageRule(),
        new Pipe2ManifestVersionPinRule(),
        new Pipe4ArchiveBinariesDependOnPathRule(),
        new Pipe5ContentPolicyAnnotationRule(),
        new Pipe1SerializerInvariantsRule(),
        new Pipe3IdentityImmutabilityRule(),
    ];
}
