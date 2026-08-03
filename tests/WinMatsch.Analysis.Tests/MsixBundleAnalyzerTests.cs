using System.Security.Cryptography;
using WinMatsch.Analysis.Msix;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class MsixBundleAnalyzerTests
{
    private const string TwoArchitectureBundleManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle" SchemaVersion="1.0">
          <Identity Name="Microsoft.Test" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.2.3.0" />
          <Packages>
            <Package Type="application" Version="1.2.3.0" Architecture="x64" FileName="app-x64.msix" Offset="70" Size="1000" />
            <Package Type="application" Version="1.2.3.0" Architecture="arm64" FileName="app-arm64.msix" Offset="1070" Size="1000" />
            <Package Type="resource" Version="1.2.3.0" ResourceId="split.language-de" FileName="res-de.msix" Offset="2070" Size="100" />
          </Packages>
        </Bundle>
        """;

    private readonly MsixBundleAnalyzer _analyzer = new();

    [Theory]
    [InlineData("app.msixbundle", true)]
    [InlineData("APP.APPXBUNDLE", true)]
    [InlineData("app.msix", false)]
    [InlineData("app.zip", false)]
    public void CanAnalyze_checks_the_extension_case_insensitively(string fileName, bool expected)
        => Assert.Equal(expected, _analyzer.CanAnalyze(fileName));

    [Fact]
    public void One_installer_per_application_architecture_sharing_family_name_and_signature()
    {
        byte[] signature = [0x30, 0x82, 0xAA, 0xBB];
        using MemoryStream bundle = MsixFixtures.BuildBundle(TwoArchitectureBundleManifest, signature);

        InstallerAnalysis analysis = _analyzer.Analyze(bundle, "app.msixbundle");

        Assert.Equal(DetectedInstallerFormat.MsixBundle, analysis.Format);
        Assert.Equal(2, analysis.Installers.Count);
        Assert.Equal(Architecture.X64, analysis.Installers[0].Architecture);
        Assert.Equal(Architecture.Arm64, analysis.Installers[1].Architecture);
        Sha256Hash expectedSignature = Sha256Hash.FromHashBytes(SHA256.HashData(signature));
        foreach (Installer installer in analysis.Installers)
        {
            Assert.Equal(InstallerType.Msix, installer.InstallerType);
            Assert.Equal("Microsoft.Test_8wekyb3d8bbwe", installer.PackageFamilyName);
            Assert.Equal(expectedSignature, installer.SignatureSha256);
        }

        Assert.Equal("1.2.3.0", analysis.ProductVersion);
    }

    [Fact]
    public void Duplicate_application_architectures_collapse_into_one_installer()
    {
        using MemoryStream bundle = MsixFixtures.BuildBundle("""
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Packages>
                <Package Type="application" Architecture="x64" FileName="a.msix" Offset="1" Size="1" />
                <Package Type="application" Architecture="x64" FileName="b.msix" Offset="2" Size="1" />
              </Packages>
            </Bundle>
            """);

        InstallerAnalysis analysis = _analyzer.Analyze(bundle, "app.msixbundle");

        Assert.Equal(Architecture.X64, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Unsigned_bundles_have_null_signature_hash()
    {
        using MemoryStream bundle = MsixFixtures.BuildBundle(TwoArchitectureBundleManifest);

        InstallerAnalysis analysis = _analyzer.Analyze(bundle, "app.msixbundle");

        Assert.All(analysis.Installers, static installer => Assert.Null(installer.SignatureSha256));
    }

    [Fact]
    public void A_bundle_with_only_resource_packages_is_rejected()
    {
        using MemoryStream bundle = MsixFixtures.BuildBundle("""
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Packages>
                <Package Type="resource" ResourceId="split.scale-200" FileName="r.msix" Offset="1" Size="1" />
              </Packages>
            </Bundle>
            """);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => _analyzer.Analyze(bundle, "app.msixbundle"));

        Assert.Contains("no application packages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_without_a_bundle_manifest_is_rejected()
    {
        using MemoryStream package = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest());

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(package, "app.msixbundle"));
    }
}
