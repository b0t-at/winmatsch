using WinMatsch.Analysis.Msi;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class MsiAnalyzerTests
{
    private static readonly (string Name, string Value)[] _typicalProperties =
    [
        ("ProductCode", "{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}"),
        ("ProductName", "Contoso Editor"),
        ("ProductVersion", "2.4.1"),
        ("Manufacturer", "Contoso Ltd"),
        ("UpgradeCode", "{C0FFEE00-1234-4321-ABCD-000000000001}"),
        ("ProductLanguage", "1033"),
    ];

    private readonly MsiAnalyzer _analyzer = new();

    [Theory]
    [InlineData("app.msi", true)]
    [InlineData("APP.MSI", true)]
    [InlineData("app.msix", false)]
    [InlineData("app.exe", false)]
    public void CanAnalyze_checks_the_extension_case_insensitively(string fileName, bool expected)
        => Assert.Equal(expected, _analyzer.CanAnalyze(fileName));

    [Fact]
    public void Properties_arp_entry_and_metadata_are_extracted()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi(_typicalProperties));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "contoso.msi");

        Assert.Equal(DetectedInstallerFormat.Msi, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", installer.ProductCode);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Editor", arp.DisplayName);
        Assert.Equal("Contoso Ltd", arp.Publisher);
        Assert.Equal("2.4.1", arp.DisplayVersion);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", arp.ProductCode);
        Assert.Equal("{C0FFEE00-1234-4321-ABCD-000000000001}", arp.UpgradeCode);
        Assert.Equal("Contoso Editor", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("2.4.1", analysis.ProductVersion);
    }

    [Theory]
    [InlineData("x64;1033", Architecture.X64)]
    [InlineData("Intel64;1033", Architecture.X64)]
    [InlineData("AMD64;1033", Architecture.X64)]
    [InlineData("Intel;1033", Architecture.X86)]
    [InlineData(";1033", Architecture.X86)]
    [InlineData("Arm64;1033", Architecture.Arm64)]
    [InlineData("Arm;1033", Architecture.Arm)]
    public void Architecture_is_mapped_from_the_template_platform(string template, Architecture expected)
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([], template: template));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Missing_summary_information_defaults_to_x86()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([], includeSummaryInformation: false));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(Architecture.X86, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Unknown_template_platform_throws_with_the_value_in_the_message()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([], template: "Sparc;1033"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(stream, "app.msi"));

        Assert.Contains("Sparc", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WiX Toolset v4")]
    [InlineData("Windows Installer XML Toolset (3.14.0.8606)")]
    public void Wix_is_detected_from_the_creating_application(string creatingApplication)
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([], creatingApplication: creatingApplication));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(InstallerType.Wix, Assert.Single(analysis.Installers).InstallerType);
    }

    [Theory]
    [InlineData("WixUI_Mode", "InstallDir")]
    [InlineData("SomeProperty", "built by WiX")]
    public void Wix_is_detected_from_property_names_and_values(string name, string value)
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([(name, value)], creatingApplication: "MSI Wrapper"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(InstallerType.Wix, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Non_wix_packages_are_plain_msi()
    {
        using var stream = new MemoryStream(
            MsiFixtures.BuildMsi([("ProductName", "Plain App")], creatingApplication: "Advanced Installer 20.0"));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(InstallerType.Msi, Assert.Single(analysis.Installers).InstallerType);
    }

    // ALLUSERS mapping: "1" is per-machine; absent or empty is Windows Installer's per-user
    // default; "2" decides at install time based on privileges, so no scope is claimed.
    [Theory]
    [InlineData("1", Scope.Machine)]
    [InlineData("", Scope.User)]
    [InlineData("2", null)]
    [InlineData("{}", null)]
    public void Scope_is_mapped_from_the_allusers_property(string allUsers, Scope? expected)
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([("ALLUSERS", allUsers)]));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(expected, Assert.Single(analysis.Installers).Scope);
    }

    [Fact]
    public void Absent_allusers_property_means_per_user()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([]));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Equal(Scope.User, Assert.Single(analysis.Installers).Scope);
    }

    [Theory]
    [InlineData("1033", "en-US")]
    [InlineData("1031", "de-DE")]
    [InlineData("3082", "es-ES")]
    [InlineData("0", null)]
    [InlineData("9999", null)]
    public void Locale_is_mapped_from_the_product_language_lcid(string productLanguage, string? expectedTag)
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([("ProductLanguage", productLanguage)]));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        LanguageTag? expected = expectedTag is null ? null : new LanguageTag(expectedTag);
        Assert.Equal(expected, Assert.Single(analysis.Installers).InstallerLocale);
    }

    [Fact]
    public void Absent_product_language_means_no_locale()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([]));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Assert.Null(Assert.Single(analysis.Installers).InstallerLocale);
    }

    [Fact]
    public void Empty_property_table_yields_no_arp_entry()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([]));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Null(analysis.ProductName);
    }

    [Fact]
    public void Long_string_refs_are_read_end_to_end()
    {
        using var stream = new MemoryStream(
            MsiFixtures.BuildMsi(_typicalProperties, template: "Arm64;1033", longStringRefs: true));

        InstallerAnalysis analysis = _analyzer.Analyze(stream, "app.msi");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.Arm64, installer.Architecture);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", installer.ProductCode);
        Assert.Equal("Contoso Editor", analysis.ProductName);
    }

    [Fact]
    public void Garbage_content_is_rejected_as_invalid_data()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(stream, "app.msi"));
    }

    [Fact]
    public void The_stream_is_left_open_after_analysis()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([]));

        _analyzer.Analyze(stream, "app.msi");

        Assert.True(stream.CanRead);
    }
}
