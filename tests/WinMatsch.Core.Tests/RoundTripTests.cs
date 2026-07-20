using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Core.Tests;

/// <summary>
/// Byte-fidelity round-trips: canonical manifests must parse into the model and serialize back
/// to the identical bytes. This is what guarantees clean diffs in winget-pkgs pull requests.
/// </summary>
public sealed class RoundTripTests
{
    private const string CanonicalInstaller = """
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        InstallerLocale: en-US
        Platform:
        - Windows.Desktop
        MinimumOSVersion: 10.0.17763.0
        InstallerType: msi
        InstallModes:
        - silent
        - silentWithProgress
        InstallerSwitches:
          Silent: /qn
          Custom: ALLUSERS=1
        InstallerSuccessCodes:
        - 0
        - 3010
        ExpectedReturnCodes:
        - InstallerReturnCode: 1603
          ReturnResponse: contactSupport
        UpgradeBehavior: install
        Protocols:
        - winmatsch
        FileExtensions:
        - wmt
        Dependencies:
          PackageDependencies:
          - PackageIdentifier: Microsoft.VCRedist.2015+.x64
            MinimumVersion: 14.0.0.0
        ProductCode: "{A1B2C3D4-1111-2222-3333-444455556666}"
        ReleaseDate: 2026-01-15
        UnsupportedOSArchitectures:
        - arm
        AppsAndFeaturesEntries:
        - DisplayName: WinMatsch Test
          Publisher: WinMatsch
          DisplayVersion: 1.2.3.0
          ProductCode: "{A1B2C3D4-1111-2222-3333-444455556666}"
          UpgradeCode: "{99999999-8888-7777-6666-555544443333}"
          InstallerType: msi
        ElevationRequirement: elevationRequired
        Installers:
        - Architecture: x64
          InstallerUrl: https://example.com/test-x64.msi
          InstallerSha256: 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
        - Architecture: x86
          Scope: user
          InstallerUrl: https://example.com/test-x86.msi
          InstallerSha256: FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210
          ReleaseDate: 2026-01-16
        ManifestType: installer
        ManifestVersion: 1.10.0
        """;

    private const string CanonicalDefaultLocale = """
        # Created with winmatsch v0.1.0
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        PackageLocale: en-US
        Publisher: WinMatsch
        PublisherUrl: https://example.com
        PublisherSupportUrl: https://example.com/support
        Author: Example Author
        PackageName: Test Package
        PackageUrl: https://example.com/test
        License: MIT
        LicenseUrl: https://example.com/license
        Copyright: Copyright (c) 2026 WinMatsch contributors
        ShortDescription: A test package for round-trip verification
        Description: |-
          First line.
          Second line.

          Fourth line after a blank one.
        Moniker: wmtest
        Tags:
        - "1.0"
        - test-tag
        - über
        ReleaseNotes: |-
          - Fixed: something important
          - Added other things
        ReleaseNotesUrl: https://example.com/notes
        ManifestType: defaultLocale
        ManifestVersion: 1.10.0
        """;

    private const string CanonicalVersion = """
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        DefaultLocale: en-US
        ManifestType: version
        ManifestVersion: 1.10.0
        """;

    [Fact]
    public void InstallerManifest_RoundTripsByteForByte()
    {
        string yaml = Canonical(CanonicalInstaller);

        InstallerManifest manifest = ManifestYamlReader.ReadInstaller(yaml);

        Assert.Equal("WinMatsch.Test", manifest.PackageIdentifier?.Value);
        Assert.Equal("1.2.3", manifest.PackageVersion?.Value);
        Assert.Equal(InstallerType.Msi, manifest.InstallerType);
        Assert.Equal([InstallMode.Silent, InstallMode.SilentWithProgress], manifest.InstallModes!);
        Assert.Equal("/qn", manifest.InstallerSwitches?.Silent);
        Assert.Equal("ALLUSERS=1", manifest.InstallerSwitches?.Custom);
        Assert.Equal([0L, 3010L], manifest.InstallerSuccessCodes!);
        Assert.Equal(ReturnResponse.ContactSupport, manifest.ExpectedReturnCodes?[0].ReturnResponse);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", manifest.Dependencies?.PackageDependencies?[0].PackageIdentifier?.Value);
        Assert.Equal(new DateOnly(2026, 1, 15), manifest.ReleaseDate);
        Assert.Equal([Architecture.Arm], manifest.UnsupportedOSArchitectures!);
        Assert.Equal("{A1B2C3D4-1111-2222-3333-444455556666}", manifest.AppsAndFeaturesEntries?[0].ProductCode);

        Assert.Equal(2, manifest.Installers?.Count);
        Assert.Equal(Architecture.X64, manifest.Installers![0].Architecture);
        Assert.Null(manifest.Installers[0].Scope);
        Assert.Equal(Scope.User, manifest.Installers[1].Scope);
        Assert.Equal(new DateOnly(2026, 1, 16), manifest.Installers[1].ReleaseDate);

        string emitted = ManifestYamlWriter.Serialize(manifest);
        Assert.Equal(yaml, emitted);
    }

    [Fact]
    public void DefaultLocaleManifest_RoundTripsByteForByte()
    {
        string yaml = Canonical(CanonicalDefaultLocale);

        DefaultLocaleManifest manifest = ManifestYamlReader.ReadDefaultLocale(yaml);

        Assert.Equal("en-US", manifest.PackageLocale?.Value);
        Assert.Equal("wmtest", manifest.Moniker);
        Assert.Equal("First line.\nSecond line.\n\nFourth line after a blank one.", manifest.Description);
        Assert.Equal("- Fixed: something important\n- Added other things", manifest.ReleaseNotes);
        Assert.Equal(["1.0", "test-tag", "über"], manifest.Tags!);

        string emitted = ManifestYamlWriter.Serialize(manifest, new ManifestWriteOptions { CreatedWith = "winmatsch v0.1.0" });
        Assert.Equal(yaml, emitted);
    }

    [Fact]
    public void VersionManifest_RoundTripsByteForByte()
    {
        string yaml = Canonical(CanonicalVersion);

        VersionManifest manifest = ManifestYamlReader.ReadVersion(yaml);

        Assert.Equal("WinMatsch.Test", manifest.PackageIdentifier?.Value);
        Assert.Equal("en-US", manifest.DefaultLocale?.Value);
        Assert.Equal(ManifestType.Version, manifest.ManifestType);

        string emitted = ManifestYamlWriter.Serialize(manifest);
        Assert.Equal(yaml, emitted);
    }

    [Fact]
    public void Serialization_IsStableAcrossRepeatedRoundTrips()
    {
        string first = ManifestYamlWriter.Serialize(ManifestYamlReader.ReadInstaller(Canonical(CanonicalInstaller)));
        string second = ManifestYamlWriter.Serialize(ManifestYamlReader.ReadInstaller(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Output_UsesLfLineEndingsAndSingleTrailingNewline()
    {
        string emitted = ManifestYamlWriter.Serialize(ManifestYamlReader.ReadVersion(Canonical(CanonicalVersion)));

        Assert.DoesNotContain('\r', emitted);
        Assert.EndsWith("\n", emitted, StringComparison.Ordinal);
        Assert.False(emitted.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void TryDetectType_IdentifiesAllManifestTypes()
    {
        Assert.Equal(ManifestType.Installer, ManifestYamlReader.TryDetectType(Canonical(CanonicalInstaller)));
        Assert.Equal(ManifestType.DefaultLocale, ManifestYamlReader.TryDetectType(Canonical(CanonicalDefaultLocale)));
        Assert.Equal(ManifestType.Version, ManifestYamlReader.TryDetectType(Canonical(CanonicalVersion)));
        Assert.Null(ManifestYamlReader.TryDetectType("just a scalar"));
        Assert.Null(ManifestYamlReader.TryDetectType("Key: value"));
    }

    [Fact]
    public void Reader_IgnoresUnknownKeys()
    {
        string yaml = Canonical(CanonicalVersion) + "SomeFutureField: whatever\n";

        VersionManifest manifest = ManifestYamlReader.ReadVersion(yaml);

        Assert.Equal("WinMatsch.Test", manifest.PackageIdentifier?.Value);
    }

    [Fact]
    public void Writer_ThrowsOnMissingRequiredFields()
    {
        var manifest = new InstallerManifest
        {
            PackageIdentifier = new PackageIdentifier("WinMatsch.Test"),
            // PackageVersion missing
            Installers = [new Installer()],
        };

        Assert.Throws<InvalidOperationException>(() => ManifestYamlWriter.Serialize(manifest));
    }

    /// <summary>Normalizes line endings and restores the trailing newline the raw string literal strips.</summary>
    private static string Canonical(string rawLiteral)
        => rawLiteral.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
