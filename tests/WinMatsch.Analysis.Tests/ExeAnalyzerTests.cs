using System.Reflection.PortableExecutable;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class ExeAnalyzerTests
{
    private readonly ExeAnalyzer _analyzer = new();

    [Theory]
    [InlineData("app.exe", true)]
    [InlineData("APP.EXE", true)]
    [InlineData(@"C:\downloads\setup.exe", true)]
    [InlineData("app.msi", false)]
    [InlineData("app.zip", false)]
    [InlineData("app", false)]
    public void CanAnalyze_checks_the_extension_case_insensitively(string fileName, bool expected)
        => Assert.Equal(expected, _analyzer.CanAnalyze(fileName));

    [Fact]
    public void Setup_keyword_in_file_description_yields_a_generic_installer_with_arp_entry()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            ProductName: "Foo",
            CompanyName: "Foo Corp",
            ProductVersion: "1.2.3",
            FileDescription: "Foo Setup"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        AppsAndFeaturesEntry entry = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Foo", entry.DisplayName);
        Assert.Equal("Foo Corp", entry.Publisher);
        Assert.Equal("1.2.3", entry.DisplayVersion);
    }

    [Theory]
    [InlineData("FooInstaller.exe")]
    [InlineData("foo-SETUP.exe")]
    [InlineData("7zs.sfx")]
    [InlineData("7zSD.sfx")]
    public void Installer_keywords_in_original_filename_are_detected(string originalFilename)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            OriginalFilename: originalFilename));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
    }

    [Fact]
    public void Generic_installer_without_version_strings_has_no_arp_entry()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            OriginalFilename: "FooSetup.exe"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Exe_without_installer_keywords_is_portable()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(
            machine: Machine.Arm64,
            version: new VersionStrings(
                ProductName: "Foo Tool",
                CompanyName: "Foo Corp",
                ProductVersion: "3.0.0",
                OriginalFilename: "footool.exe",
                FileDescription: "Foo command line tool"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "footool.exe");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.Arm64, installer.Architecture);
        Assert.Equal(InstallerType.Portable, installer.InstallerType);
        Assert.Null(installer.Commands);
        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Exe_without_any_version_resource_is_portable()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "bare.exe");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
    }

    [Fact]
    public void Elevation_requirement_flows_from_the_manifest_into_the_installer()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(
            version: new VersionStrings(FileDescription: "Foo Setup"),
            manifestXml: PeFixtures.ManifestXml("requireAdministrator"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(ElevationRequirement.ElevationRequired, installer.ElevationRequirement);
    }

    [Fact]
    public void Display_metadata_is_harvested_from_the_version_strings()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            ProductName: "Foo",
            CompanyName: "Foo Corp",
            LegalCopyright: "© Foo Corp",
            ProductVersion: "1.2.3",
            FileDescription: "Foo Setup"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.Equal("Foo", analysis.ProductName);
        Assert.Equal("Foo Corp", analysis.Publisher);
        Assert.Equal("1.2.3", analysis.ProductVersion);
        Assert.Equal("© Foo Corp", analysis.Copyright);
    }
}
