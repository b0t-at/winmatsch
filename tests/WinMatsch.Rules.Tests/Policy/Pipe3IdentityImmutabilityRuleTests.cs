using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Pipe3IdentityImmutabilityRuleTests
{
    private static readonly Pipe3IdentityImmutabilityRule _rule = new();

    [Fact]
    public void Casing_change_of_the_identifier_is_an_error()
    {
        // Motivating regression: TesseractOCR.Tesseract invented for the existing
        // tesseract-ocr.tesseract (#224123) — identity is case-sensitive and repo-resolved.
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        var lowercase = new PackageIdentifier("test.app");
        previous.Installer.PackageIdentifier = lowercase;
        previous.DefaultLocale.PackageIdentifier = lowercase;
        previous.Version.PackageIdentifier = lowercase;
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.Contains("case-sensitive", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_identifier_is_an_error()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        var moved = new PackageIdentifier("Other.App");
        previous.Installer.PackageIdentifier = moved;
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("move PR", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_manifest_identity_disagreement_is_an_error()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        current.DefaultLocale.PackageVersion = new PackageVersion("9.9.9");
        ManifestContext context = TestManifests.CreateContext(current);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("PackageVersion '9.9.9'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consistent_identity_produces_no_findings()
    {
        // Nonmatching control.
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(current, previous: previous);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Msix_entries_need_family_name_and_signature_hash()
    {
        // Motivating regression: busytag MSIX with wrong identity and incomplete evidence (#242669).
        Installer installer = TestManifests.CreateInstaller(
            installerType: InstallerType.Msix, url: "https://example.com/app.msix");
        PackageManifests current = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(current);

        _rule.Apply(context);

        Assert.Equal(2, context.Findings.Count);
        Assert.Contains(context.Findings, f => f.Message.Contains("PackageFamilyName", StringComparison.Ordinal));
        Assert.Contains(context.Findings, f => f.Message.Contains("SignatureSha256", StringComparison.Ordinal));
    }

    [Fact]
    public void Msix_family_name_must_match_analysis_evidence()
    {
        Installer installer = TestManifests.CreateInstaller(
            installerType: InstallerType.Msix, url: "https://example.com/app.msix");
        installer.PackageFamilyName = "BusyTag_abc123";
        installer.SignatureSha256 = new Sha256Hash(new string('1', Sha256Hash.Length));
        PackageManifests current = TestManifests.Create(installer);
        var evidence = new InstallerEvidence
        {
            InstallerUrl = installer.InstallerUrl!,
            Analysis = new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.Msix,
                Installers = [new Installer { PackageFamilyName = "GreynutSIA_xyz789" }],
            },
        };
        ManifestContext context = TestManifests.CreateContext(current, evidence: [evidence]);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("signed identity is authoritative", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_only_arp_entries_on_msix_are_flagged()
    {
        Installer installer = TestManifests.CreateInstaller(
            installerType: InstallerType.Msix, url: "https://example.com/app.msix");
        installer.PackageFamilyName = "App_abc";
        installer.SignatureSha256 = new Sha256Hash(new string('1', Sha256Hash.Length));
        installer.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { Publisher = "Some Publisher" }];
        PackageManifests current = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(current);

        _rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("half-filled ARP", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_msix_installers_skip_the_msix_checks()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(current);

        _rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void The_rule_never_mutates_the_manifests()
    {
        PackageManifests current = TestManifests.Create(TestManifests.CreateInstaller());
        current.DefaultLocale.PackageVersion = new PackageVersion("9.9.9");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, current, RuleMode.Apply);

        Assert.Empty(context.Changes);
        Assert.Single(context.Findings);
    }
}
