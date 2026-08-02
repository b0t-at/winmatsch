using System.Reflection.PortableExecutable;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Burn;
using WinMatsch.Analysis.Inno;
using WinMatsch.Analysis.Nsis;
using WinMatsch.Analysis.Squirrel;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class ExeAnalyzerTests
{
    private readonly ExeAnalyzer _analyzer = new();

    [Fact]
    public void Production_probe_order_is_stable_and_most_specific_first()
    {
        Type[] probeTypes = [.. ExeAnalyzer.Probes.Select(static probe => probe.GetType())];

        Assert.Equal(
            [
                typeof(AdvancedInstallerProbe),
                typeof(JavaArchiveProbe),
                typeof(SevenZipSfxProbe),
                typeof(BurnProbe),
                typeof(InnoProbe),
                typeof(NsisProbe),
                typeof(SquirrelProbe),
            ],
            probeTypes);
    }

    [Fact]
    public void Advanced_installer_fixture_is_detected_end_to_end()
        => AssertFormat(
            AdvancedInstallerFixtures.BuildInstaller(TypicalMsiProperties()),
            DetectedInstallerFormat.AdvancedInstaller);

    [Fact]
    public void Burn_fixture_is_detected_end_to_end()
        => AssertFormat(
            BurnFixtures.BuildBundle(BurnFixtures.ManifestXml()),
            DetectedInstallerFormat.Burn);

    [Fact]
    public void Inno_fixture_is_detected_end_to_end()
        => AssertFormat(InnoFixtures.BuildInstaller(), DetectedInstallerFormat.InnoSetup);

    [Fact]
    public void Nsis_fixture_is_detected_end_to_end()
        => AssertFormat(NsisFixtures.BuildInstaller(), DetectedInstallerFormat.Nullsoft);

    [Fact]
    public void Squirrel_fixture_is_detected_end_to_end()
        => AssertFormat(
            SquirrelFixtures.BuildClassicSetup(
                SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml())),
            DetectedInstallerFormat.Squirrel);

    [Fact]
    public void Seven_zip_self_extractor_uses_embedded_payload_architecture()
    {
        byte[] stub = PeFixtures.BuildExe(Machine.I386);
        byte[] archive = SevenZipFixtures.Build(("core/app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));
        using var stream = new MemoryStream([.. stub, .. archive]);

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app-installer.exe");

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.True(analysis.IsSelfExtractorStub);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void Java_archive_wrapper_with_multi_architecture_native_libraries_is_neutral()
    {
        byte[] stub = PeFixtures.BuildExe(Machine.I386);
        using MemoryStream jar = DependencyFixtures.BuildZip(
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nMain-Class: example.Main\n"u8.ToArray()),
            ("example/Main.class", [0xCA, 0xFE, 0xBA, 0xBE]),
            ("native/win32-x86/jnidispatch.dll", [1]),
            ("native/win32-x86-64/jnidispatch.dll", [1]),
            ("native/win32-aarch64/jnidispatch.dll", [1]));
        using var stream = new MemoryStream([.. stub, .. jar.ToArray()]);

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "hmcl.exe");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
        Assert.Equal(Architecture.Neutral, Assert.Single(analysis.Installers).Architecture);
        Assert.Equal("JAVA001", Assert.Single(analysis.Diagnostics).Code);
    }

    [Fact]
    public void Advanced_installer_wins_when_the_stub_also_has_squirrel_markers()
        => AssertFormat(
            AdvancedInstallerFixtures.BuildInstaller(
                TypicalMsiProperties(),
                version: SquirrelFixtures.BrandedStub),
            DetectedInstallerFormat.AdvancedInstaller);

    [Fact]
    public void Embedded_probe_magic_in_a_managed_wrapper_does_not_claim_inno()
    {
        using FileStream stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Analysis.dll"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "single-file-wrapper.exe");

        Assert.NotEqual(DetectedInstallerFormat.InnoSetup, analysis.Format);
    }

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
    [InlineData("7z.sfx")]
    [InlineData("7zCon.sfx")]
    [InlineData("7zs.sfx")]
    [InlineData("7zSD.sfx")]
    public void Installer_keywords_in_original_filename_are_detected(string originalFilename)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            OriginalFilename: originalFilename));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
    }

    [Theory]
    [InlineData("7z.sfx")]
    [InlineData("7zCon.sfx")]
    [InlineData("7zS.sfx")]
    [InlineData("7zs.sfx")]
    [InlineData("7zsd.sfx")]
    [InlineData("7zS2.sfx")]
    [InlineData("7zS2con.sfx")]
    public void Seven_zip_self_extractors_are_marked_as_stub_version_sources(string originalFilename)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            OriginalFilename: originalFilename,
            ProductVersion: "24.09",
            FileVersion: "24.09"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.True(analysis.IsSelfExtractorStub);
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

    [Theory]
    [InlineData("153.0.7986.0_chrome_installer_uncompressed.exe")]
    [InlineData("FooSetup.exe")]
    [InlineData(@"C:\downloads\chrome_installer.exe")]
    public void Installer_keyword_in_the_file_name_yields_a_generic_installer(string fileName)
    {
        // Google's uncompressed Chrome installer carries no version resource at all.
        using MemoryStream stream = PeFixtures.BuildExeStream();

        InstallerAnalysis analysis = _analyzer.Analyze(stream, fileName);

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
        Assert.Equal(InstallerType.Exe, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Installer_keyword_in_the_directory_only_does_not_claim_an_installer()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();

        InstallerAnalysis analysis = _analyzer.Analyze(stream, @"C:\setup-files\tool.exe");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
    }

    [Fact]
    public void File_name_is_ignored_when_the_version_resource_has_evidence()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            ProductName: "Foo Tool"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "setup.exe");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
    }

    [Theory]
    [InlineData("tool-x64.exe", Architecture.X64)]
    [InlineData("tool-aarch64.exe", Architecture.Arm64)]
    public void Explicit_filename_architecture_overrides_an_x86_generic_wrapper(
        string fileName,
        Architecture expected)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(machine: Machine.I386);

        InstallerAnalysis analysis = _analyzer.Analyze(stream, fileName);

        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("ARCH001", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
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
            FileVersion: "1.2.3.4",
            FileDescription: "Foo Setup"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "foo.exe");

        Assert.Equal("Foo", analysis.ProductName);
        Assert.Equal("Foo Corp", analysis.Publisher);
        Assert.Equal("1.2.3", analysis.ProductVersion);
        Assert.Equal("1.2.3.4", analysis.FileVersion);
        Assert.Equal("© Foo Corp", analysis.Copyright);
    }

    private void AssertFormat(byte[] content, DetectedInstallerFormat expected)
    {
        using var stream = new MemoryStream(content);

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "fixture.exe");

        Assert.Equal(expected, analysis.Format);
    }

    private static (string Name, string Value)[] TypicalMsiProperties() =>
    [
        ("ProductCode", "{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}"),
        ("ProductName", "Contoso Editor"),
        ("ProductVersion", "2.5.0"),
        ("Manufacturer", "Contoso Ltd"),
        ("ProductLanguage", "1033"),
        ("ALLUSERS", "1"),
    ];

}
