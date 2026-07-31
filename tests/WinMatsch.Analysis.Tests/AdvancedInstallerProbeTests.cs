using System.Reflection.PortableExecutable;
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
    public void Plain_pe_without_overlay_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new AdvancedInstallerProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Overlay_without_7z_signature_returns_null()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(new byte[256]);

        Assert.Null(Probe(sfx));
    }

    [Fact]
    public void Sfx_with_embedded_msi_is_detected_with_payload_evidence()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.AdvancedInstaller, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal("{5A2FEA1B-0F30-4F86-9F92-01A45C5A1E30}", installer.ProductCode);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        Assert.Equal("/exenoui /qn", installer.InstallerSwitches!.Silent);
        Assert.Equal("/exebasicui /qb", installer.InstallerSwitches.SilentWithProgress);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Editor", arp.DisplayName);
        Assert.Equal("Contoso Ltd", arp.Publisher);
    }

    [Fact]
    public void Outer_version_strings_win_over_inner_msi_metadata()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(
            _typicalProperties,
            version: AdvancedInstallerFixtures.BrandedStub);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal("Contoso Studio", analysis.ProductName);
        Assert.Equal("3.1.0", analysis.ProductVersion);
        // The inner MSI is WiX-built, but the outer container decides the classification.
        Assert.Equal(InstallerType.Exe, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Inner_msi_metadata_fills_gaps_when_the_stub_has_no_version_strings()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal("Contoso Editor", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("2.5.0", analysis.ProductVersion);
    }

    // The MSI's summary-information template names the payload's architecture; the 32-bit
    // stub is just the bootstrapper and must not decide it.
    [Theory]
    [InlineData("Intel;1033", Architecture.X86)]
    [InlineData("x64;1033", Architecture.X64)]
    [InlineData("Arm64;1033", Architecture.Arm64)]
    public void Architecture_comes_from_the_inner_msi_template(string template, Architecture expected)
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller([], template: template, machine: Machine.I386);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Readable_archive_without_msi_and_without_marker_returns_null()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(
            SevenZipFixtures.Build(("readme.txt", "hello"u8.ToArray())));

        Assert.Null(Probe(sfx));
    }

    [Fact]
    public void Branded_stub_without_msi_payload_still_claims_the_format()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(
            SevenZipFixtures.Build(("readme.txt", "hello"u8.ToArray())),
            version: AdvancedInstallerFixtures.BrandedStub);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.AdvancedInstaller, analysis.Format);
        Assert.Equal("Contoso Studio", analysis.ProductName);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Corrupt_archive_with_branding_throws()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(
            CorruptSevenZipOverlay(),
            version: AdvancedInstallerFixtures.BrandedStub);

        Assert.Throws<InvalidDataException>(() => Probe(sfx));
    }

    [Fact]
    public void Corrupt_archive_without_branding_returns_null()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(CorruptSevenZipOverlay());

        Assert.Null(Probe(sfx));
    }

    [Fact]
    public void Oversized_declared_msi_degrades_to_outer_metadata_without_extraction()
    {
        byte[] archive = SevenZipFixtures.Build(
            [("product.msi", MsiFixtures.BuildMsi(_typicalProperties))],
            firstEntryDeclaredSize: 512L * 1024 * 1024);
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(archive, version: AdvancedInstallerFixtures.BrandedStub);

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.AdvancedInstaller, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
    }

    [Fact]
    public void Corrupt_inner_msi_payload_throws()
    {
        byte[] archive = SevenZipFixtures.Build(("product.msi", "this is not an msi"u8.ToArray()));
        byte[] sfx = AdvancedInstallerFixtures.BuildSfx(archive);

        Assert.Throws<InvalidDataException>(() => Probe(sfx));
    }

    [Fact]
    public void Elevation_manifest_of_the_stub_is_reported()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(
            _typicalProperties,
            manifestXml: PeFixtures.ManifestXml("requireAdministrator"));

        InstallerAnalysis? analysis = Probe(sfx);

        Assert.NotNull(analysis);
        Assert.Equal(
            ElevationRequirement.ElevationRequired,
            Assert.Single(analysis.Installers).ElevationRequirement);
    }

    // Precedence evidence: the other exe probes must not claim an Advanced Installer SFX,
    // so registering AdvancedInstallerProbe ahead of them can never mask a real match.
    [Fact]
    public void Other_exe_probes_do_not_claim_the_sfx()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);

        using var stream = new MemoryStream(sfx);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        Assert.Null(new BurnProbe().Probe(peFile, stream));
        stream.Position = 0;
        Assert.Null(new NsisProbe().Probe(peFile, stream));
    }

    [Fact]
    public void The_stream_is_left_open_after_probing()
    {
        byte[] sfx = AdvancedInstallerFixtures.BuildInstaller(_typicalProperties);

        using var stream = new MemoryStream(sfx);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        new AdvancedInstallerProbe().Probe(peFile, stream);

        Assert.True(stream.CanRead);
    }

    /// <summary>A 7z signature followed by garbage instead of a parseable archive.</summary>
    private static byte[] CorruptSevenZipOverlay()
    {
        byte[] overlay = new byte[128];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }.CopyTo(overlay, 0);
        for (int i = 6; i < overlay.Length; i++)
        {
            overlay[i] = 0xFF;
        }

        return overlay;
    }

    private static InstallerAnalysis? Probe(byte[] sfx)
    {
        using var stream = new MemoryStream(sfx);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new AdvancedInstallerProbe().Probe(peFile, stream);
    }
}
