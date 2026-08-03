using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Scope2ExplicitScopeFromEvidenceRuleTests
{
    private const string Url = "https://example.com/app-x64.msi";

    private static Scope2ExplicitScopeFromEvidenceRule CreateRule(
        Scope scope,
        PolicyScopeEvidenceOrigin origin,
        string source = "MSI ALLUSERS=1")
        => new(new PolicyEvidence
        {
            InstallerScopes = new Dictionary<string, PolicyScopeEvidence>(StringComparer.OrdinalIgnoreCase)
            {
                [Url] = new PolicyScopeEvidence { Scope = scope, Origin = origin, Source = source },
            },
        });

    [Fact]
    public void Trusted_msi_evidence_sets_machine_scope()
    {
        // Motivating regression: missing Scope: machine for RancherDesktop MSI (#224118).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.MsiAllUsersProperty).Apply(context);

        Assert.Equal(Scope.Machine, manifests.Installer.Installers![0].Scope);
    }

    [Fact]
    public void Trusted_inno_evidence_sets_user_scope()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Scope.User, PolicyScopeEvidenceOrigin.InnoPrivilegesRequired, "Inno PrivilegesRequired=lowest").Apply(context);

        Assert.Equal(Scope.User, manifests.Installer.Installers![0].Scope);
    }

    [Fact]
    public void Wrapper_metadata_evidence_is_never_trusted()
    {
        // "Never infer from generic wrapper metadata" — the evidence is reported and ignored.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.WrapperMetadata, "embedded MSI metadata").Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("not trusted", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_evidence_nothing_happens()
    {
        // Nonmatching control.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Scope2ExplicitScopeFromEvidenceRule().Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Existing_scope_is_never_overwritten_and_conflicts_are_flagged()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: Url, scope: Scope.User));
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.MsiAllUsersProperty).Apply(context);

        Assert.Equal(Scope.User, manifests.Installer.Installers![0].Scope);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("review required", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_scope_counts_as_existing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));
        manifests.Installer.Scope = Scope.Machine;
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.MsiAllUsersProperty).Apply(context);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.MsiAllUsersProperty), manifests, RuleMode.LogOnly);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal(RuleMode.LogOnly, change.Mode);
        Assert.Contains("ALLUSERS", change.SourceEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller(url: Url));

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            CreateRule(Scope.Machine, PolicyScopeEvidenceOrigin.MsiAllUsersProperty), manifests, RuleMode.Disabled);

        Assert.Null(manifests.Installer.Installers![0].Scope);
        Assert.Empty(context.Changes);
    }
}
