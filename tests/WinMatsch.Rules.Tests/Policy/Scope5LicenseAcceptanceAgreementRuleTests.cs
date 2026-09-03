using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Scope5LicenseAcceptanceAgreementRuleTests
{
    private static readonly Scope5LicenseAcceptanceAgreementRule _rule = new();

    [Theory]
    [InlineData("/i // /qn accept_eula=1")]
    [InlineData("/S /ACCEPTEULA")]
    [InlineData("--accept-license --silent")]
    [InlineData("/qn IACCEPTLICENSE=YES")]
    [InlineData("/qn AGREETOLICENSE=1")]
    [InlineData("/qn EULA_ACCEPTED=1")]
    public void Licence_acceptance_switch_without_agreements_is_flagged(string silent)
    {
        // Motivating regression: DiRoots.ProSheets carried `accept_eula=1` without Agreements (#420154).
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Exe);
        installer.InstallerSwitches = new InstallerSwitches { Silent = silent };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Scope5, finding.RuleId);
        Assert.Equal(RuleSeverity.Warning, finding.Severity);
        Assert.Equal("Installers[0].InstallerSwitches.Silent", finding.Path);
        Assert.Contains("Agreements", finding.Message, StringComparison.Ordinal);
        Assert.Equal(silent, installer.InstallerSwitches.Silent);
    }

    [Fact]
    public void Declared_agreement_satisfies_the_rule()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Exe);
        installer.InstallerSwitches = new InstallerSwitches { Silent = "/qn accept_eula=1" };
        PackageManifests manifests = TestManifests.Create(installer);
        manifests.DefaultLocale.Agreements =
        [
            new PackageAgreement
            {
                AgreementLabel = "EULA",
                AgreementUrl = "https://example.com/eula",
            },
        ];
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("/qn ADDLOCAL=ALL SHOWLICENSE=0")]
    [InlineData("--license-key=abc --silent")]
    public void Ordinary_switches_are_untouched(string silent)
    {
        // Nonmatching control.
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Exe);
        installer.InstallerSwitches = new InstallerSwitches { Silent = silent };
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Root_and_custom_switches_are_inspected()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.InstallerSwitches = new InstallerSwitches { Custom = "ACCEPT_EULA=1" };
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal("InstallerSwitches.Custom", finding.Path);
    }
}
