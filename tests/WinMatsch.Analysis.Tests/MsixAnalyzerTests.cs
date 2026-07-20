using System.Security.Cryptography;
using WinMatsch.Analysis.Msix;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class MsixPackageFamilyNameTests
{
    [Fact]
    public void Microsoft_publisher_hashes_to_the_well_known_publisher_id()
        => Assert.Equal("8wekyb3d8bbwe", MsixPackageFamilyName.ComputePublisherId(MsixFixtures.MicrosoftPublisher));

    [Fact]
    public void Family_name_joins_identity_name_and_publisher_id()
        => Assert.Equal(
            "Microsoft.Test_8wekyb3d8bbwe",
            MsixPackageFamilyName.Create("Microsoft.Test", MsixFixtures.MicrosoftPublisher));
}

public class MsixAnalyzerTests
{
    private readonly MsixAnalyzer _analyzer = new();

    [Theory]
    [InlineData("app.msix", true)]
    [InlineData("APP.MSIX", true)]
    [InlineData("app.appx", true)]
    [InlineData("app.msixbundle", false)]
    [InlineData("app.zip", false)]
    public void CanAnalyze_checks_the_extension_case_insensitively(string fileName, bool expected)
        => Assert.Equal(expected, _analyzer.CanAnalyze(fileName));

    [Fact]
    public void Identity_metadata_and_family_name_are_extracted()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(
            identityName: "Microsoft.Test",
            publisher: MsixFixtures.MicrosoftPublisher,
            version: "1.2.3.0"));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal(DetectedInstallerFormat.Msix, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(InstallerType.Msix, installer.InstallerType);
        Assert.Equal("Microsoft.Test_8wekyb3d8bbwe", installer.PackageFamilyName);
        Assert.Equal("Contoso Editor", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("1.2.3.0", analysis.ProductVersion);
    }

    [Theory]
    [InlineData("x86", Architecture.X86)]
    [InlineData("arm", Architecture.Arm)]
    [InlineData("arm64", Architecture.Arm64)]
    [InlineData("neutral", Architecture.Neutral)]
    [InlineData(null, Architecture.Neutral)] // Absent attribute: the schema default is neutral.
    public void Architecture_is_mapped_from_the_identity(string? token, Architecture expected)
    {
        using MemoryStream package = MsixFixtures.BuildPackage(
            MsixFixtures.PackageManifest(processorArchitecture: token));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Unknown_architecture_throws_with_the_value_in_the_message()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(
            MsixFixtures.PackageManifest(processorArchitecture: "sparc"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => _analyzer.Analyze(package, "app.msix"));

        Assert.Contains("sparc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_hash_is_computed_from_the_p7x_entry()
    {
        byte[] signature = [0x30, 0x82, 0x01, 0x02, 0x03, 0x04];
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(), signature);

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal(
            Sha256Hash.FromHashBytes(SHA256.HashData(signature)),
            Assert.Single(analysis.Installers).SignatureSha256);
    }

    [Fact]
    public void Missing_signature_yields_null_hash()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest());

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Null(Assert.Single(analysis.Installers).SignatureSha256);
    }

    [Fact]
    public void Target_device_families_map_to_platforms_and_the_minimum_os_version()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(dependencies: """
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.22000.0" MaxVersionTested="10.0.22621.0" />
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
            <TargetDeviceFamily Name="Windows.Team" MinVersion="10.0.16299.0" MaxVersionTested="10.0.22621.0" />
            """));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal([Platform.WindowsUniversal, Platform.WindowsDesktop], installer.Platform);
        Assert.Equal(new MinimumOSVersion("10.0.16299.0"), installer.MinimumOSVersion);
    }

    [Fact]
    public void Capabilities_are_split_into_regular_and_restricted()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(capabilities: """
            <Capability Name="internetClient" />
            <uap:Capability Name="documentsLibrary" />
            <rescap:Capability Name="runFullTrust" />
            <rescap:Capability Name="allowElevation" />
            <DeviceCapability Name="webcam" />
            """));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(["internetClient", "documentsLibrary"], installer.Capabilities);
        Assert.Equal(["runFullTrust", "allowElevation"], installer.RestrictedCapabilities);
    }

    [Fact]
    public void Packages_without_capabilities_have_null_lists()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest());

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.Capabilities);
        Assert.Null(installer.RestrictedCapabilities);
    }

    [Fact]
    public void Pre_1809_families_without_msix_mentions_are_appx()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(dependencies: """
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.14393.0" MaxVersionTested="10.0.16299.0" />
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.16299.0" MaxVersionTested="10.0.16299.0" />
            """));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.appx");

        Assert.Equal(InstallerType.Appx, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void A_family_at_or_above_1809_makes_the_package_msix()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(dependencies: """
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.14393.0" MaxVersionTested="10.0.22621.0" />
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
            """));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal(InstallerType.Msix, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void An_msix_mention_makes_a_pre_1809_package_msix()
    {
        string manifest = MsixFixtures.PackageManifest(dependencies: """
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.14393.0" MaxVersionTested="10.0.16299.0" />
            """).Replace(
            "xmlns:uap=",
            "xmlns:msix=\"http://schemas.microsoft.com/msix/msixpackaginginfo\" xmlns:uap=",
            StringComparison.Ordinal);
        using MemoryStream package = MsixFixtures.BuildPackage(manifest);

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal(InstallerType.Msix, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void No_target_device_families_means_msix()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(dependencies: null));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Msix, installer.InstallerType);
        Assert.Null(installer.Platform);
        Assert.Null(installer.MinimumOSVersion);
    }

    [Fact]
    public void Ms_resource_display_names_pass_through()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(
            MsixFixtures.PackageManifest(displayName: "ms-resource:AppName"));

        InstallerAnalysis analysis = _analyzer.Analyze(package, "app.msix");

        Assert.Equal("ms-resource:AppName", analysis.ProductName);
    }

    [Fact]
    public void A_zip_without_a_manifest_is_rejected()
    {
        using MemoryStream package = MsixFixtures.BuildBundle("<Bundle />");

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(package, "app.msix"));
    }
}
