using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Pipe4ArchiveBinariesDependOnPathRuleTests
{
    private const string Url = "https://example.com/app-win-x64.zip";

    private static Installer CreateZipPortable()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Zip, url: Url);
        installer.NestedInstallerType = InstallerType.Portable;
        installer.NestedInstallerFiles = [new NestedInstallerFile { RelativeFilePath = "app/app.exe" }];
        return installer;
    }

    private static Pipe4ArchiveBinariesDependOnPathRule CreateRule(params string[] urls)
        => new(new PolicyEvidence { SiblingImportUrls = urls });

    [Fact]
    public void Sibling_import_evidence_sets_the_flag_on_zip_portable_entries()
    {
        // Motivating regression: hdrview needed ArchiveBinariesDependOnPath (#203020).
        PackageManifests manifests = TestManifests.Create(CreateZipPortable());
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Url).Apply(context);

        Assert.True(manifests.Installer.Installers![0].ArchiveBinariesDependOnPath);
    }

    [Fact]
    public void Without_evidence_the_flag_is_never_set()
    {
        // Nonmatching control: same shape, no supplied evidence.
        PackageManifests manifests = TestManifests.Create(CreateZipPortable());
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Pipe4ArchiveBinariesDependOnPathRule().Apply(context);

        Assert.Null(manifests.Installer.Installers![0].ArchiveBinariesDependOnPath);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Evidence_for_a_non_zip_portable_entry_only_produces_a_finding()
    {
        Installer installer = TestManifests.CreateInstaller(installerType: InstallerType.Exe, url: Url);
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Url).Apply(context);

        Assert.Null(installer.ArchiveBinariesDependOnPath);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("not a zip-portable", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_already_true_effective_value_is_left_alone()
    {
        Installer installer = CreateZipPortable();
        PackageManifests manifests = TestManifests.Create(installer);
        manifests.Installer.ArchiveBinariesDependOnPath = true;

        ManifestContext context = PolicyTestSupport.RunViaPipeline(CreateRule(Url), manifests, RuleMode.Apply);

        Assert.Null(installer.ArchiveBinariesDependOnPath);
        Assert.Empty(context.Changes);
    }

    [Fact]
    public void An_explicit_false_is_never_flipped_silently()
    {
        Installer installer = CreateZipPortable();
        installer.ArchiveBinariesDependOnPath = false;
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        CreateRule(Url).Apply(context);

        // An explicit false is a human decision: the contradiction is reported, never flipped.
        Assert.False(installer.ArchiveBinariesDependOnPath);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("explicitly false", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = TestManifests.Create(CreateZipPortable());

        ManifestContext context = PolicyTestSupport.RunViaPipeline(CreateRule(Url), manifests, RuleMode.LogOnly);

        Assert.Null(manifests.Installer.Installers![0].ArchiveBinariesDependOnPath);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal(RuleMode.LogOnly, change.Mode);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(CreateZipPortable());

        ManifestContext context = PolicyTestSupport.RunViaPipeline(CreateRule(Url), manifests, RuleMode.Disabled);

        Assert.Null(manifests.Installer.Installers![0].ArchiveBinariesDependOnPath);
        Assert.Empty(context.Changes);
    }
}
