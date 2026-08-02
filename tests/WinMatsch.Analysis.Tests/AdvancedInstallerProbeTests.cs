using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Burn;
using WinMatsch.Analysis.Nsis;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class AdvancedInstallerProbeTests
{
    private static readonly (string Name, string Value)[] _typicalProperties =
    [
        ("ProductCode", "{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}"),
        ("ProductName", "Contoso Editor"),
        ("ProductVersion", "2.5.0"),
        ("Manufacturer", "Contoso Ltd"),
        ("ProductLanguage", "1033"),
        ("ALLUSERS", "1"),
    ];

    [Fact]
    public void Plain_pe_without_footer_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new AdvancedInstallerProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Arbitrary_raw_overlay_7z_sfx_is_not_advanced_installer()
    {
        byte[] archive = SevenZipFixtures.Build(
            ("product.msi", MsiFixtures.BuildMsi(_typicalProperties, "x64;1033")));

        Assert.Null(Probe(AdvancedInstallerFixtures.BuildRawOverlay(archive)));
    }

    [Fact]
    public void Footer_text_without_a_valid_self_pointer_is_not_sufficient()
    {
        Assert.Null(Probe(AdvancedInstallerFixtures.BuildRawOverlay("ADVINSTSFX"u8.ToArray())));
    }

    [Fact]
    public void Direct_msi_record_is_detected_by_type_even_with_a_bin_name()
    {
        InstallerAnalysis? analysis = Probe(AdvancedInstallerFixtures.BuildInstaller(_typicalProperties));

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.AdvancedInstaller, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", installer.ProductCode);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        Assert.Equal("/exenoui /qn", installer.InstallerSwitches!.Silent);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Editor", arp.DisplayName);
    }

    [Fact]
    public void Nested_7z_record_and_xor_flag_are_honored()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(
            _typicalProperties,
            nestedSevenZip: true,
            xorPayload: true);

        Installer installer = Assert.Single(Probe(setup)!.Installers);

        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", installer.ProductCode);
    }

    [Fact]
    public void Msi_extension_on_a_non_msi_record_does_not_supply_identity()
    {
        byte[] msi = MsiFixtures.BuildMsi(_typicalProperties, "x64;1033");
        byte[] setup = AdvancedInstallerFixtures.BuildContainer(
            [new AdvancedInstallerFixtures.FixtureEntry(0, 3, 0, "decoy.msi", msi)],
            version: AdvancedInstallerFixtures.BrandedStub);

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(setup));

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Equal("Contoso Studio", analysis.ProductName);
    }

    [Fact]
    public void Outer_version_strings_win_over_visible_inner_msi_metadata()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(
            _typicalProperties,
            version: AdvancedInstallerFixtures.BrandedStub);

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(setup));

        Assert.Equal("Contoso Studio", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("3.1.0", analysis.ProductVersion);
        Assert.Equal(InstallerType.Exe, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Visible_inner_msi_metadata_fills_outer_gaps()
    {
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(AdvancedInstallerFixtures.BuildInstaller(_typicalProperties)));

        Assert.Equal("Contoso Editor", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("2.5.0", analysis.ProductVersion);
    }

    [Fact]
    public void Hidden_inner_msi_does_not_leak_product_code_or_arp_identity()
    {
        (string Name, string Value)[] hidden = [.. _typicalProperties, ("ARPSYSTEMCOMPONENT", "1")];

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(AdvancedInstallerFixtures.BuildInstaller(hidden)));

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Null(analysis.ProductName);
        Assert.Null(analysis.Publisher);
        Assert.Null(analysis.ProductVersion);
    }

    [Theory]
    [InlineData("Intel;1033", Architecture.X86)]
    [InlineData("x64;1033", Architecture.X64)]
    [InlineData("Arm64;1033", Architecture.Arm64)]
    public void Architecture_comes_from_the_inner_msi(string template, Architecture expected)
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller([], template: template, machine: Machine.I386);

        Assert.Equal(expected, Assert.Single(Probe(setup)!.Installers).Architecture);
    }

    [Fact]
    public void Structurally_identified_container_with_a_truncated_table_throws()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);
        int signature = setup.AsSpan().LastIndexOf("ADVINSTSFX"u8);
        int footer = signature - 64;
        BitConverter.GetBytes((uint)(footer - 2)).CopyTo(setup, footer + 20);

        Assert.Throws<InvalidDataException>(() => Probe(setup));
    }

    [Fact]
    public void Corrupt_direct_msi_throws_an_explicit_error()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildContainer(
            [new AdvancedInstallerFixtures.FixtureEntry(1, 0, 0, "bad.bin", "not an msi"u8.ToArray())]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Probe(setup));
        Assert.Contains("embedded MSI", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_nested_7z_throws_an_explicit_error()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildContainer(
            [new AdvancedInstallerFixtures.FixtureEntry(3, 7, 0, "bad.dat", "not a 7z"u8.ToArray())]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Probe(setup));
        Assert.Contains("nested 7z", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_declared_msi_table_stream_is_rejected_before_allocation()
    {
        byte[] msi = MsiFixtures.BuildMsi(_typicalProperties);
        string encodedName = MsiFixtures.EncodeStreamName("Property", isTable: true);
        byte[] nameBytes = Encoding.Unicode.GetBytes(encodedName + "\0");
        int directoryEntry = msi.AsSpan().IndexOf(nameBytes);
        Assert.True(directoryEntry >= 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            msi.AsSpan(directoryEntry + 120),
            (ulong)AnalysisLimits.MaxMsiStreamBytes + 1);
        using var stream = new MemoryStream(msi);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => AdvancedMsiProperties.IsArpSystemComponent(stream));

        Assert.Contains("ends before its declared size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Elevation_manifest_of_the_stub_is_reported()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(
            _typicalProperties,
            manifestXml: PeFixtures.ManifestXml("requireAdministrator"));

        Assert.Equal(
            ElevationRequirement.ElevationRequired,
            Assert.Single(Probe(setup)!.Installers).ElevationRequirement);
    }

    [Fact]
    public void Other_exe_probes_do_not_claim_the_container()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);

        Assert.Null(new BurnProbe().Probe(peFile, stream));
        stream.Position = 0;
        Assert.Null(new NsisProbe().Probe(peFile, stream));
    }

    [Fact]
    public void The_stream_is_left_open_after_probing()
    {
        byte[] setup = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);

        new AdvancedInstallerProbe().Probe(peFile, stream);

        Assert.True(stream.CanRead);
    }

    private static InstallerAnalysis? Probe(byte[] setup)
    {
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new AdvancedInstallerProbe().Probe(peFile, stream);
    }
}
