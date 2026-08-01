using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using Xunit;

namespace WinMatsch.Rules.Tests;

public sealed class ApplyOverridePackFieldsRuleTests
{
    [Fact]
    public void Applies_scope_layout_metadata_replacements_and_preserved_fields()
    {
        PackageManifests previous = Create("1.0.0");
        previous.Installer.Scope = Scope.Machine;
        previous.Installer.Installers![0].InstallerSwitches = new InstallerSwitches { Silent = "/quiet" };
        previous.DefaultLocale.PublisherUrl = "http://old.example.test";
        PackageManifests current = Create("2.0.0");
        current.Installer.Scope = Scope.Machine;
        current.DefaultLocale.PublisherUrl = "http://old.example.test";
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            ScopeLayout = ScopeLayoutOverride.PerInstaller,
            MetadataUrlReplacements = ImmutableDictionary.CreateRange(
                [KeyValuePair.Create("http://old.example.test", "https://new.example.test")]),
            PreservedFields = ["Installers[*].InstallerSwitches"],
        };
        var context = new ManifestContext
        {
            Manifests = current,
            Previous = previous,
        };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Null(current.Installer.Scope);
        Assert.Equal(Scope.Machine, current.Installer.Installers![0].Scope);
        Assert.Equal("/quiet", current.Installer.Installers[0].InstallerSwitches?.Silent);
        Assert.Equal("https://new.example.test", current.DefaultLocale.PublisherUrl);
    }

    [Fact]
    public void Explicit_drop_wins_over_preservation()
    {
        PackageManifests previous = Create("1.0.0");
        previous.DefaultLocale.ReleaseNotes = "Keep this";
        PackageManifests current = Create("2.0.0");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            PreservedFields = ["DefaultLocale.ReleaseNotes"],
            DroppedFields = ["DefaultLocale.ReleaseNotes"],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Null(current.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Root_scope_layout_rejects_mixed_effective_scopes()
    {
        PackageManifests current = Create("2.0.0");
        current.Installer.Installers!.Add(new Installer
        {
            Architecture = Architecture.X86,
            InstallerType = InstallerType.Exe,
            Scope = Scope.User,
            InstallerUrl = "https://example.test/app-x86.exe",
            InstallerSha256 = new Sha256Hash(new string('B', 64)),
        });
        current.Installer.Installers[0].Scope = Scope.Machine;
        var context = new ManifestContext { Manifests = current };
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            ScopeLayout = ScopeLayoutOverride.Root,
        };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Contains(context.Findings, finding => finding.Message.Contains("same explicit effective scope", StringComparison.Ordinal));
        Assert.Null(current.Installer.Scope);
    }

    private static PackageManifests Create(string versionValue)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion(versionValue);
        var locale = new LanguageTag("en-US");
        return new()
        {
            Version = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                        InstallerUrl = "https://example.test/app-x64.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                    },
                ],
            },
            DefaultLocale = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example",
            },
            Locales = [],
        };
    }
}
