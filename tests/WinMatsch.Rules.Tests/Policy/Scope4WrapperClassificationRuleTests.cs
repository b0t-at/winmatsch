using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Scope4WrapperClassificationRuleTests
{
    private static readonly Scope4WrapperClassificationRule _rule = new();

    private const string Url = "https://example.com/app-setup.exe";

    private static InstallerEvidence Evidence(DetectedInstallerFormat format) => new()
    {
        InstallerUrl = Url,
        Analysis = new InstallerAnalysis
        {
            Format = format,
            Installers = [new Installer()],
        },
    };

    [Fact]
    public void Burn_wrapper_misclassified_as_wix_is_corrected_and_product_code_dropped()
    {
        // Motivating regression: EPOS Connect exe wrapper detected as wix + inner-MSI ProductCode (#174323).
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        installer.ProductCode = "{56C3E1E0-1111-2222-3333-444455556666}";
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Burn)]);

        _rule.Apply(context);

        Assert.Equal(InstallerType.Burn, installer.InstallerType);
        Assert.Null(installer.ProductCode);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("embedded", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Squirrel_wrapper_misclassified_as_wix_becomes_exe_and_machine_scope_is_flagged()
    {
        // Motivating regression: GitHub Desktop Beta squirrel exe detected as wix/machine (#156239).
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url, scope: Scope.Machine);
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Squirrel)]);

        _rule.Apply(context);

        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        RuleFinding finding = Assert.Single(context.Findings, f => f.Message.Contains("per-user", StringComparison.Ordinal));
        Assert.Equal(RuleCatalogueIds.Scope4, finding.RuleId);
    }

    [Fact]
    public void Genuine_msi_is_untouched()
    {
        // Nonmatching control: the analyzer confirmed a real MSI.
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Msi, url: Url);
        installer.ProductCode = "{ABC00000-1111-2222-3333-444455556666}";
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Msi)]);

        _rule.Apply(context);

        Assert.Equal(InstallerType.Msi, installer.InstallerType);
        Assert.NotNull(installer.ProductCode);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Without_analysis_evidence_nothing_is_reclassified()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(InstallerType.Wix, installer.InstallerType);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Non_msi_declared_types_are_left_alone()
    {
        // The rule only rescues msi/wix misclassifications; a declared nullsoft entry is not touched
        // even when analysis says burn — that conflict belongs to WM0103 InstallerTypeConsistency.
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Nullsoft, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Burn)]);

        _rule.Apply(context);

        Assert.Equal(InstallerType.Nullsoft, installer.InstallerType);
    }

    [Fact]
    public void Root_product_code_is_cleared_when_every_entry_is_reclassified()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);
        manifests.Installer.ProductCode = "{56C3E1E0-1111-2222-3333-444455556666}";
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Burn)]);

        _rule.Apply(context);

        Assert.Null(manifests.Installer.ProductCode);
        Assert.Contains(context.Findings, f => f.Message.Contains("root ProductCode", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_product_code_shared_with_genuine_msi_entries_is_flagged_not_cleared()
    {
        Installer wrapper = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        Installer genuineMsi = TestManifests.CreateInstaller(Architecture.X86, InstallerType.Msi, "https://example.com/app-x86.msi");
        PackageManifests manifests = TestManifests.Create(wrapper, genuineMsi);
        manifests.Installer.ProductCode = "{56C3E1E0-1111-2222-3333-444455556666}";
        ManifestContext context = TestManifests.CreateContext(
            manifests, evidence: [Evidence(DetectedInstallerFormat.Burn)]);

        _rule.Apply(context);

        Assert.Equal("{56C3E1E0-1111-2222-3333-444455556666}", manifests.Installer.ProductCode);
        Assert.Contains(context.Findings, f => f.Message.Contains("review whether it belongs", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);

        var context = new ManifestContext
        {
            Manifests = manifests,
            Evidence = [Evidence(DetectedInstallerFormat.Burn)],
            Options = new RuleOptions { Explain = true },
        };
        var runtime = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode> { [_rule.Id] = RuleMode.LogOnly });
        RulePipeline.Create([_rule], runtime).Run(context);

        Assert.Equal(InstallerType.Wix, installer.InstallerType);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Wix, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            _rule, manifests, RuleMode.Disabled, evidence: [Evidence(DetectedInstallerFormat.Burn)]);

        Assert.Equal(InstallerType.Wix, installer.InstallerType);
        Assert.Empty(context.Changes);
    }
}
