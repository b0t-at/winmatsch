using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class Wm0007PreserveOnUpdateTests
{
    private static readonly PreserveOnUpdateRule _rule = new();

    [Fact]
    public void Copies_switches_and_dependencies_from_the_matching_previous_installer()
    {
        Installer previousInstaller = TestManifests.CreateInstaller();
        previousInstaller.InstallerSwitches = new InstallerSwitches { Custom = "/norestart" };
        previousInstaller.Dependencies = new Dependencies { ExternalDependencies = ["vcredist"] };
        PackageManifests previous = TestManifests.Create(previousInstaller);

        Installer installer = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(installer);

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Equal("/norestart", installer.InstallerSwitches?.Custom);
        Assert.Equal("vcredist", Assert.Single(installer.Dependencies!.ExternalDependencies!));
        Assert.NotSame(previousInstaller.InstallerSwitches, installer.InstallerSwitches);
        Assert.NotSame(previousInstaller.Dependencies, installer.Dependencies);
    }

    [Fact]
    public void Does_not_overwrite_values_the_new_manifest_already_has()
    {
        Installer previousInstaller = TestManifests.CreateInstaller();
        previousInstaller.InstallerSwitches = new InstallerSwitches { Custom = "/old" };
        PackageManifests previous = TestManifests.Create(previousInstaller);

        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSwitches = new InstallerSwitches { Custom = "/new" };
        PackageManifests manifests = TestManifests.Create(installer);

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Equal("/new", installer.InstallerSwitches.Custom);
    }

    [Fact]
    public void Installer_without_a_matching_previous_entry_gets_nothing()
    {
        Installer previousInstaller = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        previousInstaller.InstallerSwitches = new InstallerSwitches { Custom = "/x86-only" };
        PackageManifests previous = TestManifests.Create(previousInstaller);

        Installer installer = TestManifests.CreateInstaller(Architecture.X64);
        PackageManifests manifests = TestManifests.Create(installer);

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Null(installer.InstallerSwitches);
    }

    [Fact]
    public void Previous_root_defaults_are_looked_through_when_matching_and_copying()
    {
        Installer previousInstaller = TestManifests.CreateInstaller(installerType: null);
        PackageManifests previous = TestManifests.Create(previousInstaller);
        previous.Installer.InstallerType = InstallerType.Msi;
        previous.Installer.InstallerSwitches = new InstallerSwitches { Silent = "/qn" };

        Installer installer = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(installer);

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Equal("/qn", installer.InstallerSwitches?.Silent);
    }

    [Fact]
    public void Copies_hand_maintained_default_locale_fields_left_null()
    {
        PackageManifests previous = TestManifests.Create(TestManifests.CreateInstaller());
        previous.DefaultLocale.Moniker = "testapp";
        previous.DefaultLocale.PublisherUrl = "https://example.com";
        previous.DefaultLocale.Tags = ["editor"];
        previous.DefaultLocale.Documentations = [new Documentation { DocumentLabel = "Wiki", DocumentUrl = "https://example.com/wiki" }];

        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Equal("testapp", manifests.DefaultLocale.Moniker);
        Assert.Equal("https://example.com", manifests.DefaultLocale.PublisherUrl);
        Assert.Equal("editor", Assert.Single(manifests.DefaultLocale.Tags!));
        Assert.NotSame(previous.DefaultLocale.Tags, manifests.DefaultLocale.Tags);
        Assert.Equal("Wiki", Assert.Single(manifests.DefaultLocale.Documentations!).DocumentLabel);
        Assert.NotSame(previous.DefaultLocale.Documentations[0], manifests.DefaultLocale.Documentations![0]);
    }

    [Fact]
    public void Version_specific_release_notes_url_is_not_carried_over()
    {
        PackageManifests previous = TestManifests.Create(TestManifests.CreateInstaller());
        previous.DefaultLocale.ReleaseNotesUrl = $"https://example.com/releases/v{TestManifests.DefaultVersion}";
        previous.DefaultLocale.ReleaseNotes = "old notes";

        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Null(manifests.DefaultLocale.ReleaseNotesUrl);
        Assert.Null(manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Version_free_release_notes_url_is_carried_over()
    {
        PackageManifests previous = TestManifests.Create(TestManifests.CreateInstaller());
        previous.DefaultLocale.ReleaseNotesUrl = "https://example.com/changelog";

        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());

        _rule.Apply(TestManifests.CreateContext(manifests, previous: previous));

        Assert.Equal("https://example.com/changelog", manifests.DefaultLocale.ReleaseNotesUrl);
    }

    [Fact]
    public void No_op_without_previous_manifests()
    {
        Installer installer = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(installer);
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Null(installer.InstallerSwitches);
        Assert.Empty(context.Trace);
    }
}
