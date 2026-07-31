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
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        Channel: preview
        InstallerLocale: en-US
        Platform:
        - Windows.Desktop
        MinimumOSVersion: 10.0.17763.0
        InstallerType: msi
        NestedInstallerType: font
        NestedInstallerFiles:
        - RelativeFilePath: payload/test.exe
          PortableCommandAlias: wmtest
        Scope: machine
        InstallModes:
        - silent
        - silentWithProgress
        InstallerSwitches:
          Silent: /qn
          SilentWithProgress: /qb
          Interactive: /passive
          InstallLocation: INSTALLDIR="<INSTALLPATH>"
          Log: /log "<LOGPATH>"
          Upgrade: /upgrade
          Custom: ALLUSERS=1
          Repair: /repair
        InstallerSuccessCodes:
        - 0
        - 3010
        ExpectedReturnCodes:
        - InstallerReturnCode: 1603
          ReturnResponse: contactSupport
          ReturnResponseUrl: https://example.com/errors/1603
        UpgradeBehavior: install
        Commands:
        - wmtest
        Protocols:
        - winmatsch
        FileExtensions:
        - wmt
        Dependencies:
          WindowsFeatures:
          - NetFx3
          WindowsLibraries:
          - Microsoft.VCLibs.140.00
          PackageDependencies:
          - PackageIdentifier: Microsoft.VCRedist.2015+.x64
            MinimumVersion: 14.0.0.0
          ExternalDependencies:
          - Example runtime
        PackageFamilyName: WinMatsch.Test_123456789abcd
        ProductCode: "{A1B2C3D4-1111-2222-3333-444455556666}"
        Capabilities:
        - internetClient
        RestrictedCapabilities:
        - runFullTrust
        Markets:
          AllowedMarkets:
          - AT
          - US
        InstallerAbortsTerminal: true
        ReleaseDate: 2026-01-15
        InstallLocationRequired: true
        RequireExplicitUpgrade: false
        DisplayInstallWarnings: true
        UnsupportedOSArchitectures:
        - arm
        UnsupportedArguments:
        - log
        AppsAndFeaturesEntries:
        - DisplayName: WinMatsch Test
          Publisher: WinMatsch
          DisplayVersion: 1.2.3.0
          ProductCode: "{A1B2C3D4-1111-2222-3333-444455556666}"
          UpgradeCode: "{99999999-8888-7777-6666-555544443333}"
          InstallerType: msi
        ElevationRequirement: elevationRequired
        InstallationMetadata:
          DefaultInstallLocation: "%ProgramFiles%\\WinMatsch"
          Files:
          - RelativeFilePath: wmtest.exe
            FileSha256: ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789
            FileType: launch
            InvocationParameter: --gui
            DisplayName: WinMatsch Test
        DownloadCommandProhibited: false
        RepairBehavior: installer
        ArchiveBinariesDependOnPath: true
        Authentication:
          AuthenticationType: microsoftEntraId
          MicrosoftEntraIdAuthenticationInfo:
            Resource: https://example.com
            Scope: package.read
        Installers:
        - Architecture: x64
          InstallerUrl: https://example.com/test-x64.msi
          InstallerSha256: 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
          SignatureSha256: "1111111111111111111111111111111111111111111111111111111111111111"
        - Architecture: x86
          Scope: user
          InstallerUrl: https://example.com/test-x86.msi
          InstallerSha256: FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210
          Markets:
            ExcludedMarkets:
            - CN
          ReleaseDate: 2026-01-16
        ManifestType: installer
        ManifestVersion: 1.12.0
        """;

    private const string CanonicalDefaultLocale = """
        # Created with winmatsch v0.1.0
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        PackageLocale: en-US
        Publisher: WinMatsch
        PublisherUrl: https://example.com
        PublisherSupportUrl: https://example.com/support
        PrivacyUrl: https://example.com/privacy
        Author: Example Author
        PackageName: Test Package
        PackageUrl: https://example.com/test
        License: MIT
        LicenseUrl: https://example.com/license
        Copyright: Copyright (c) 2026 WinMatsch contributors
        CopyrightUrl: https://example.com/copyright
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
        Agreements:
        - AgreementLabel: Test terms
          Agreement: |-
            Read these terms.
            Accept them.
          AgreementUrl: https://example.com/terms
        ReleaseNotes: |-
          - Fixed: something important
          - Added other things
        ReleaseNotesUrl: https://example.com/notes
        PurchaseUrl: https://example.com/buy
        InstallationNotes: Restart the terminal after installation.
        Documentations:
        - DocumentLabel: User guide
          DocumentUrl: https://example.com/docs
        Icons:
        - IconUrl: https://example.com/icon.png
          IconFileType: png
          IconResolution: 16x16
          IconTheme: highContrast
          IconSha256: "2222222222222222222222222222222222222222222222222222222222222222"
        ManifestType: defaultLocale
        ManifestVersion: 1.12.0
        """;

    private const string CanonicalLocale = """
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.locale.1.12.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        PackageLocale: de-DE
        Publisher: WinMatsch
        PackageName: Testpaket
        License: MIT
        ShortDescription: Ein Testpaket
        Tags:
        - test
        Icons:
        - IconUrl: https://example.com/icon.ico
          IconFileType: ico
          IconResolution: 256x256
          IconTheme: default
        ManifestType: locale
        ManifestVersion: 1.12.0
        """;

    private const string CanonicalVersion = """
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json

        PackageIdentifier: WinMatsch.Test
        PackageVersion: 1.2.3
        DefaultLocale: en-US
        ManifestType: version
        ManifestVersion: 1.12.0
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
        Assert.Equal("https://example.com/errors/1603", manifest.ExpectedReturnCodes?[0].ReturnResponseUrl);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", manifest.Dependencies?.PackageDependencies?[0].PackageIdentifier?.Value);
        Assert.Equal(InstallerType.Font, manifest.NestedInstallerType);
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
        Assert.Equal(IconResolution.Size16, manifest.Icons?[0].IconResolution);

        string emitted = ManifestYamlWriter.Serialize(manifest, new ManifestWriteOptions { CreatedWith = "winmatsch v0.1.0" });
        Assert.Equal(yaml, emitted);
    }

    [Fact]
    public void LocaleManifest_RoundTripsByteForByte()
    {
        string yaml = Canonical(CanonicalLocale);

        LocaleManifest manifest = ManifestYamlReader.ReadLocale(yaml);

        Assert.Equal("de-DE", manifest.PackageLocale?.Value);
        Assert.Equal(IconResolution.Size256, manifest.Icons?[0].IconResolution);
        Assert.Equal(yaml, ManifestYamlWriter.Serialize(manifest));
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

    [Fact]
    public void Writer_ThrowsOnWrongManifestType()
    {
        VersionManifest manifest = ManifestYamlReader.ReadVersion(Canonical(CanonicalVersion));
        manifest.ManifestType = ManifestType.Locale;

        Assert.Throws<InvalidOperationException>(() => ManifestYamlWriter.Serialize(manifest));
    }

    [Theory]
    [InlineData(InstallerType.Zip)]
    [InlineData(InstallerType.Pwa)]
    public void NestedInstallerType_RejectsValuesOutsideSchemaSubset(InstallerType installerType)
    {
        InstallerManifest manifest = ManifestYamlReader.ReadInstaller(Canonical(CanonicalInstaller));
        manifest.NestedInstallerType = installerType;

        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestYamlWriter.Serialize(manifest));
        Assert.Throws<FormatException>(() => ManifestYamlReader.ReadInstaller(
            Canonical(CanonicalInstaller).Replace(
                "NestedInstallerType: font",
                $"NestedInstallerType: {installerType.ToYaml()}",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void UnsupportedOSArchitectures_RejectsNeutral()
    {
        InstallerManifest manifest = ManifestYamlReader.ReadInstaller(Canonical(CanonicalInstaller));
        manifest.UnsupportedOSArchitectures = [Architecture.Neutral];

        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestYamlWriter.Serialize(manifest));
        Assert.Throws<FormatException>(() => ManifestYamlReader.ReadInstaller(
            Canonical(CanonicalInstaller).Replace(
                "UnsupportedOSArchitectures:\n- arm",
                "UnsupportedOSArchitectures:\n- neutral",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void Markets_RejectsBothMutuallyExclusiveLists()
    {
        InstallerManifest manifest = ManifestYamlReader.ReadInstaller(Canonical(CanonicalInstaller));
        manifest.Markets!.ExcludedMarkets = ["CN"];

        Assert.Throws<InvalidOperationException>(() => ManifestYamlWriter.Serialize(manifest));
    }

    [Fact]
    public void Markets_EmptyPresentList_RoundTrips()
    {
        string yaml = Canonical(CanonicalInstaller).Replace(
            "AllowedMarkets:\n  - AT\n  - US",
            "AllowedMarkets: []",
            StringComparison.Ordinal);

        InstallerManifest manifest = ManifestYamlReader.ReadInstaller(yaml);

        Assert.Empty(manifest.Markets!.AllowedMarkets!);
        Assert.Equal(yaml, ManifestYamlWriter.Serialize(manifest));
    }

    /// <summary>Normalizes line endings and restores the trailing newline the raw string literal strips.</summary>
    private static string Canonical(string rawLiteral)
        => rawLiteral.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
