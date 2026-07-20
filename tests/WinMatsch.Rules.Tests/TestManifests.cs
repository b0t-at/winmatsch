using WinMatsch.Core;

namespace WinMatsch.Rules.Tests;

/// <summary>Builders for the minimal manifest shapes the rule tests operate on.</summary>
internal static class TestManifests
{
    public const string DefaultPackageName = "Test App";
    public const string DefaultPublisher = "Test Publisher";
    public const string DefaultVersion = "1.2.3";

    public static PackageManifests Create(params Installer[] installers)
    {
        var identifier = new PackageIdentifier("Test.App");
        var version = new PackageVersion(DefaultVersion);
        var locale = new LanguageTag("en-US");
        return new PackageManifests
        {
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                Installers = [.. installers],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                PackageName = DefaultPackageName,
                Publisher = DefaultPublisher,
                License = "MIT",
                ShortDescription = "A test app.",
            },
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
        };
    }

    public static Installer CreateInstaller(
        Architecture architecture = Architecture.X64,
        InstallerType? installerType = InstallerType.Msi,
        string? url = "https://example.com/app-x64.msi",
        Scope? scope = null)
        => new()
        {
            Architecture = architecture,
            InstallerType = installerType,
            InstallerUrl = url,
            Scope = scope,
        };

    public static ManifestContext CreateContext(
        PackageManifests manifests,
        bool explain = true,
        PackageManifests? previous = null,
        IReadOnlyList<InstallerEvidence>? evidence = null)
        => new()
        {
            Manifests = manifests,
            Previous = previous,
            Evidence = evidence ?? [],
            Options = new RuleOptions { Explain = explain },
        };
}
