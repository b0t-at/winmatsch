using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class ManifestPathsTests
{
    [Fact]
    public void GetPackageDirectory_UsesLowercaseFirstCharacterBucket()
    {
        var identifier = new PackageIdentifier("Microsoft.PowerToys");
        Assert.Equal("manifests/m/Microsoft/PowerToys", ManifestPaths.GetPackageDirectory(identifier));
    }

    [Fact]
    public void GetPackageDirectory_KeepsDigitBucket()
    {
        var identifier = new PackageIdentifier("7zip.7zip");
        Assert.Equal("manifests/7/7zip/7zip", ManifestPaths.GetPackageDirectory(identifier));
    }

    [Fact]
    public void GetVersionDirectory_AppendsRawVersion()
    {
        var identifier = new PackageIdentifier("Microsoft.PowerToys");
        var version = new PackageVersion("0.75.1");
        Assert.Equal("manifests/m/Microsoft/PowerToys/0.75.1", ManifestPaths.GetVersionDirectory(identifier, version));
    }

    [Fact]
    public void FileNames_FollowWingetPkgsConventions()
    {
        var identifier = new PackageIdentifier("Microsoft.PowerToys");

        Assert.Equal("Microsoft.PowerToys.installer.yaml", ManifestPaths.GetInstallerFileName(identifier));
        Assert.Equal("Microsoft.PowerToys.yaml", ManifestPaths.GetVersionFileName(identifier));
        Assert.Equal("Microsoft.PowerToys.locale.zh-CN.yaml", ManifestPaths.GetLocaleFileName(identifier, new LanguageTag("zh-CN")));
    }
}
