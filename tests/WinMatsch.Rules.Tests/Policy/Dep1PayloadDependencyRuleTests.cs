using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Dep1PayloadDependencyRuleTests
{
    private const string Url = "https://example.com/app-x64.exe";

    private static Dep1PayloadDependencyRule CreateRule(params DependencyEvidence[] evidence)
        => new(new PolicyEvidence
        {
            DependencyAnalyses = new Dictionary<string, PayloadDependencyAnalysis>(StringComparer.OrdinalIgnoreCase)
            {
                [Url] = new PayloadDependencyAnalysis(evidence),
            },
        });

    private static DependencyEvidence Evidence(
        DependencyEvidenceKind kind,
        DependencyEvidenceStatus status,
        Architecture? architecture = Architecture.X64,
        int? runtimeMajor = null,
        string[]? signals = null) => new()
        {
            PayloadPath = "app.exe",
            Kind = kind,
            Status = status,
            Architecture = architecture,
            RuntimeMajor = runtimeMajor,
            Signals = signals ?? [],
        };

    private static PackageManifests CreateManifests(Architecture architecture = Architecture.X64)
        => TestManifests.Create(TestManifests.CreateInstaller(architecture, InstallerType.Exe, Url));

    [Fact]
    public void Detected_vcredist_evidence_adds_an_architecture_matched_dependency()
    {
        // Motivating regression: manual Microsoft.VCRedist.2015+.x64 additions (tealdeer #245546 et al.).
        PackageManifests manifests = CreateManifests();
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected,
                signals: ["vcruntime140.dll"]));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        PackageDependency dependency = Assert.Single(
            manifests.Installer.Installers![0].Dependencies!.PackageDependencies!);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", dependency.PackageIdentifier?.Value);
    }

    [Fact]
    public void Detected_dotnet_evidence_adds_a_runtime_major_dependency()
    {
        PackageManifests manifests = CreateManifests();
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.DotNetRuntime, DependencyEvidenceStatus.Detected, runtimeMajor: 8));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        PackageDependency dependency = Assert.Single(
            manifests.Installer.Installers![0].Dependencies!.PackageDependencies!);
        Assert.Equal("Microsoft.DotNet.Runtime.8", dependency.PackageIdentifier?.Value);
    }

    [Theory]
    [InlineData(DependencyEvidenceStatus.Inferred)]
    [InlineData(DependencyEvidenceStatus.Ambiguous)]
    [InlineData(DependencyEvidenceStatus.Unavailable)]
    public void Weak_evidence_never_becomes_a_mandatory_dependency(DependencyEvidenceStatus status)
    {
        PackageManifests manifests = CreateManifests();
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, status));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Dependencies);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Info, finding.Severity);
        Assert.Contains("not strong enough", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_mismatched_evidence_adds_nothing()
    {
        // Motivating regression: VCRedist x64 vs needed x86 (Oracle MySQL #154168, died unmerged).
        PackageManifests manifests = CreateManifests(Architecture.X86);
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected, Architecture.X64));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Dependencies);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("does not match", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_dependency_is_not_duplicated()
    {
        PackageManifests manifests = CreateManifests();
        manifests.Installer.Installers![0].Dependencies = new Dependencies
        {
            PackageDependencies = [new PackageDependency { PackageIdentifier = new PackageIdentifier("Microsoft.VCRedist.2015+.x64") }],
        };
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Single(manifests.Installer.Installers![0].Dependencies!.PackageDependencies!);
    }

    [Fact]
    public void Root_dependencies_are_cloned_before_adding_a_per_installer_dependency()
    {
        // Creating a bare per-installer Dependencies object must not mask the manifest-root
        // defaults (WindowsFeatures etc.) that applied to this installer.
        PackageManifests manifests = CreateManifests();
        manifests.Installer.Dependencies = new Dependencies
        {
            WindowsFeatures = ["NetFx3"],
            PackageDependencies = [new PackageDependency { PackageIdentifier = new PackageIdentifier("Some.Base") }],
        };
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Dependencies dependencies = manifests.Installer.Installers![0].Dependencies!;
        Assert.Equal(["NetFx3"], dependencies.WindowsFeatures);
        Assert.Equal(2, dependencies.PackageDependencies!.Count);
        Assert.Contains(dependencies.PackageDependencies, d => d.PackageIdentifier?.Value == "Some.Base");
        Assert.Contains(dependencies.PackageDependencies, d => d.PackageIdentifier?.Value == "Microsoft.VCRedist.2015+.x64");
    }

    [Fact]
    public void Conflicting_dotnet_majors_are_never_stacked()
    {
        // Detected .NET 8 next to a carried .NET 5 pin must not produce two mandatory runtimes.
        PackageManifests manifests = CreateManifests();
        manifests.Installer.Installers![0].Dependencies = new Dependencies
        {
            PackageDependencies = [new PackageDependency { PackageIdentifier = new PackageIdentifier("Microsoft.DotNet.Runtime.5") }],
        };
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.DotNetRuntime, DependencyEvidenceStatus.Detected, runtimeMajor: 8));
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        PackageDependency dependency = Assert.Single(manifests.Installer.Installers![0].Dependencies!.PackageDependencies!);
        Assert.Equal("Microsoft.DotNet.Runtime.5", dependency.PackageIdentifier?.Value);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("conflicts with the already-declared dependency", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Previous_dotnet_major_change_is_flagged_for_verification()
    {
        // Motivating regression: FamiStudio .NET 5 -> 8 major bump (#203022).
        PackageManifests manifests = CreateManifests();
        Installer previousInstaller = TestManifests.CreateInstaller(Architecture.X64, InstallerType.Exe, "https://example.com/app-old.exe");
        previousInstaller.Dependencies = new Dependencies
        {
            PackageDependencies = [new PackageDependency { PackageIdentifier = new PackageIdentifier("Microsoft.DotNet.Runtime.5") }],
        };
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", previousInstaller);
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.DotNetRuntime, DependencyEvidenceStatus.Detected, runtimeMajor: 8));
        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("pinned .NET runtime major 5", finding.Message, StringComparison.Ordinal);
        Assert.Contains("major 8", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_supplied_analysis_nothing_happens()
    {
        // Nonmatching control.
        PackageManifests manifests = CreateManifests();
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Dep1PayloadDependencyRule().Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Dependencies);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = CreateManifests();
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.LogOnly);

        Assert.Null(manifests.Installer.Installers![0].Dependencies);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = CreateManifests();
        Dep1PayloadDependencyRule rule = CreateRule(
            Evidence(DependencyEvidenceKind.VisualCppRuntime, DependencyEvidenceStatus.Detected));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.Disabled);

        Assert.Null(manifests.Installer.Installers![0].Dependencies);
        Assert.Empty(context.Changes);
    }
}
