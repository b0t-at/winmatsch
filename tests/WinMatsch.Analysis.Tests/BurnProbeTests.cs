using System.Reflection.PortableExecutable;
using WinMatsch.Analysis.Burn;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class BurnProbeTests
{
    [Fact]
    public void Non_burn_pe_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new BurnProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Wrong_section_magic_returns_null()
        => Assert.Null(Probe(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), magic: 0x1234ABCD)));

    [Theory]
    [InlineData(BurnFixtures.Wix3Namespace)]
    [InlineData(BurnFixtures.Wix4Namespace)]
    public void Registration_arp_and_related_bundle_are_extracted(string xmlns)
    {
        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(xmlns: xmlns)));

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Burn, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Burn, installer.InstallerType);
        Assert.Equal(Architecture.X86, installer.Architecture);
        Assert.Equal(BurnFixtures.BundleProductCode, installer.ProductCode);
        Assert.Null(installer.ElevationRequirement);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Suite", arp.DisplayName);
        Assert.Equal("Contoso Ltd", arp.Publisher);
        Assert.Equal("2.5.0", arp.DisplayVersion);
        Assert.Equal(BurnFixtures.BundleProductCode, arp.ProductCode);
        Assert.Equal(BurnFixtures.BundleUpgradeCode, arp.UpgradeCode);
        Assert.Equal("Contoso Suite", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("2.5.0", analysis.ProductVersion);
    }

    [Fact]
    public void Mszip_compressed_ux_container_parses()
    {
        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), msZip: true));

        Assert.NotNull(analysis);
        Assert.Equal("Contoso Suite", analysis.ProductName);
    }

    [Fact]
    public void Arp_register_no_omits_the_arp_entry()
    {
        string manifest = BurnFixtures.ManifestXml(
            arpXml: """<Arp Register="no" DisplayName="Contoso Runtime" Publisher="Contoso Ltd" />""");

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Equal(BurnFixtures.BundleProductCode, installer.ProductCode);
        Assert.Equal("Contoso Runtime", analysis.ProductName);
    }

    [Fact]
    public void Missing_arp_falls_back_to_the_version_strings()
    {
        byte[] bundle = BurnFixtures.BuildBundle(
            BurnFixtures.ManifestXml(arpXml: null, registrationVersion: null),
            version: new VersionStrings(
                ProductName: "Stub Product",
                CompanyName: "Stub Co",
                ProductVersion: "9.9.9",
                LegalCopyright: "© Stub Co"));

        InstallerAnalysis? analysis = Probe(bundle);

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Installers).AppsAndFeaturesEntries);
        Assert.Equal("Stub Product", analysis.ProductName);
        Assert.Equal("Stub Co", analysis.Publisher);
        Assert.Equal("9.9.9", analysis.ProductVersion);
        Assert.Equal("© Stub Co", analysis.Copyright);
    }

    [Fact]
    public void Arp_display_version_falls_back_to_the_registration_version()
    {
        string manifest = BurnFixtures.ManifestXml(
            arpXml: """<Arp Register="yes" DisplayName="Contoso Suite" Publisher="Contoso Ltd" />""");

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        AppsAndFeaturesEntry arp = Assert.Single(Assert.Single(analysis.Installers).AppsAndFeaturesEntries!);
        Assert.Equal("2.5.0.0", arp.DisplayVersion);
        Assert.Equal("2.5.0.0", analysis.ProductVersion);
    }

    [Fact]
    public void Missing_related_bundle_leaves_the_upgrade_code_null()
    {
        InstallerAnalysis? analysis = Probe(
            BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(includeRelatedBundle: false)));

        Assert.NotNull(analysis);
        AppsAndFeaturesEntry arp = Assert.Single(Assert.Single(analysis.Installers).AppsAndFeaturesEntries!);
        Assert.Null(arp.UpgradeCode);
    }

    [Fact]
    public void Wix4_relation_type_is_accepted_for_upgrade_code()
    {
        string manifest = BurnFixtures.ManifestXml()
            .Replace("Action=\"Upgrade\"", "RelationType=\"Upgrade\"", StringComparison.Ordinal);

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        AppsAndFeaturesEntry arp = Assert.Single(Assert.Single(analysis.Installers).AppsAndFeaturesEntries!);
        Assert.Equal(BurnFixtures.BundleUpgradeCode, arp.UpgradeCode);
    }

    [Theory]
    [InlineData(Machine.I386, Architecture.X86)]
    [InlineData(Machine.Amd64, Architecture.X64)]
    [InlineData(Machine.Arm64, Architecture.Arm64)]
    public void Architecture_defaults_to_the_stub_machine(Machine machine, Architecture expected)
    {
        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), machine: machine));

        Assert.NotNull(analysis);
        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Theory]
    [InlineData("NativeMachine = 0xAA64", Architecture.Arm64)]
    [InlineData("(NativeMachine = arm64)", Architecture.Arm64)]
    [InlineData("VersionNT64", Architecture.X64)]
    [InlineData("NativeMachine = 0x8664", Architecture.X64)]
    public void Chain_install_conditions_override_the_x86_stub_machine(string condition, Architecture expected)
    {
        string manifest = BurnFixtures.ManifestXml(installCondition: condition);

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest, machine: Machine.I386));

        Assert.NotNull(analysis);
        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Visible_inner_msi_is_correlated_without_changing_the_outer_type()
    {
        const string innerProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
        const string innerUpgradeCode = "{11111111-AAAA-BBBB-CCCC-222222222222}";
        string manifest = BurnFixtures.ManifestXml(msiPackageXml:
            $"""<MsiPackage Id="Main" ProductCode="{innerProductCode}" UpgradeCode="{innerUpgradeCode}" DisplayName="Contoso Engine" Version="4.2.0" Visible="yes" />""");

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Burn, installer.InstallerType);
        Assert.Equal(BurnFixtures.BundleProductCode, installer.ProductCode);
        AppsAndFeaturesEntry inner = Assert.Single(installer.AppsAndFeaturesEntries!, entry => entry.ProductCode == innerProductCode);
        Assert.Equal("Contoso Engine", inner.DisplayName);
        Assert.Equal(innerUpgradeCode, inner.UpgradeCode);
        Assert.Equal(InstallerType.Msi, inner.InstallerType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hidden_inner_msi_does_not_create_an_arp_entry(bool arpSystemComponent)
    {
        string package = arpSystemComponent
            ? """<MsiPackage Id="Hidden" ProductCode="{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}" Visible="yes"><MsiProperty Id="ARPSYSTEMCOMPONENT" Value="1" /></MsiPackage>"""
            : """<MsiPackage Id="Hidden" ProductCode="{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}" Visible="no" />""";
        string manifest = BurnFixtures.ManifestXml(msiPackageXml: package);

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        AppsAndFeaturesEntry arp = Assert.Single(Assert.Single(analysis.Installers).AppsAndFeaturesEntries!);
        Assert.Equal(BurnFixtures.BundleProductCode, arp.ProductCode);
    }

    [Fact]
    public void Multi_architecture_chain_retains_outer_architecture_and_requests_review()
    {
        string manifest = BurnFixtures.ManifestXml(msiPackageXml: """
            <MsiPackage Id="X64" ProductCode="{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}" InstallCondition="VersionNT64" />
            <MsiPackage Id="Arm64" ProductCode="{11111111-BBBB-CCCC-DDDD-222222222222}" InstallCondition="NativeMachine = 0xAA64" />
            """);

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.X86, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("BURN001", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
    }

    [Fact]
    public void Non_msi_chain_package_contributes_architecture_evidence()
    {
        string manifest = BurnFixtures.ManifestXml(msiPackageXml:
            """<ExePackage Id="Payload" InstallCondition="NativeMachine = 0xAA64" />""");

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.Arm64, Assert.Single(analysis.Installers).Architecture);
    }

    [Theory]
    [InlineData("NativeMachine &lt;&gt; 0xAA64")]
    [InlineData("NOT (NativeMachine = arm64)")]
    [InlineData("NOT VersionNT64")]
    [InlineData("VersionNT64 OR NativeMachine = arm64")]
    [InlineData("VersionNT64 AND WixBundleInstalled")]
    [InlineData("NativeMachine = 0xAA64 OR WixBundleAction = 2")]
    public void Negated_architecture_condition_does_not_promote_the_stub(string condition)
    {
        string manifest = BurnFixtures.ManifestXml(msiPackageXml:
            $"""<ExePackage Id="Payload" InstallCondition="{condition}" />""");

        InstallerAnalysis? analysis = Probe(BurnFixtures.BuildBundle(manifest));

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.X86, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("BURN002", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
    }

    [Fact]
    public void Elevation_requirement_comes_from_the_application_manifest()
    {
        byte[] bundle = BurnFixtures.BuildBundle(
            BurnFixtures.ManifestXml(),
            appManifestXml: PeFixtures.ManifestXml("requireAdministrator"));

        InstallerAnalysis? analysis = Probe(bundle);

        Assert.NotNull(analysis);
        Assert.Equal(ElevationRequirement.ElevationRequired, Assert.Single(analysis.Installers).ElevationRequirement);
    }

    [Fact]
    public void Unsupported_section_version_throws_with_the_version_in_the_message()
    {
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), sectionVersion: 3);

        var exception = Assert.Throws<InvalidDataException>(() => Probe(bundle));

        Assert.Contains("version 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_container_format_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), containerFormat: 2);

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void Zero_containers_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), containerSizes: []);

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void Truncated_attached_container_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml());

        Assert.Throws<InvalidDataException>(() => Probe(bundle[..^16]));
    }

    [Fact]
    public void Corrupt_ux_container_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), uxContainer: new byte[64]);

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void Ux_container_without_manifest_file_throws()
    {
        byte[] cabinet = BurnFixtures.BuildCabinet([("1", "payload"u8.ToArray())]);
        byte[] bundle = BurnFixtures.BuildBundle(BurnFixtures.ManifestXml(), uxContainer: cabinet);

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void Malformed_manifest_xml_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle("<BurnManifest attribute=");

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void Wrong_manifest_root_element_throws()
    {
        byte[] bundle = BurnFixtures.BuildBundle("""<Wrong xmlns="http://schemas.microsoft.com/wix/2008/Burn" />""");

        Assert.Throws<InvalidDataException>(() => Probe(bundle));
    }

    [Fact]
    public void The_stream_is_left_open()
    {
        using var stream = new MemoryStream(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml()));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        InstallerAnalysis? analysis = new BurnProbe().Probe(peFile, stream);

        Assert.NotNull(analysis);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
    }

    [Fact]
    public void ExeAnalyzer_detects_burn_bundles_end_to_end()
    {
        using var stream = new MemoryStream(BurnFixtures.BuildBundle(BurnFixtures.ManifestXml()));

        InstallerAnalysis analysis = new ExeAnalyzer().Analyze(stream, "contoso-setup.exe");

        Assert.Equal(DetectedInstallerFormat.Burn, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Burn, installer.InstallerType);
        Assert.Equal(BurnFixtures.BundleProductCode, installer.ProductCode);
    }

    private static InstallerAnalysis? Probe(byte[] bundle)
    {
        using var stream = new MemoryStream(bundle);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new BurnProbe().Probe(peFile, stream);
    }
}
