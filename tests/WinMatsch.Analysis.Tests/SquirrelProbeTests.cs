using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Analysis.Pe;
using WinMatsch.Analysis.Squirrel;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class SquirrelProbeTests
{
    [Fact]
    public void Plain_pe_without_overlay_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new SquirrelProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Classic_setup_wrapping_a_nupkg_is_detected()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal("Contoso.Chat", installer.ProductCode);
        Assert.Equal("--silent", installer.InstallerSwitches!.Silent);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Chat", arp.DisplayName);
        Assert.Equal("Contoso Ltd", arp.Publisher);
        Assert.Equal("1.2.3", arp.DisplayVersion);
        Assert.Equal("Contoso.Chat", arp.ProductCode);
        Assert.Equal("Contoso Chat", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("1.2.3", analysis.ProductVersion);
    }

    [Fact]
    public void Clowd_setup_with_the_nupkg_as_overlay_is_detected()
    {
        byte[] setup = SquirrelFixtures.BuildClowdSetup(SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal("Contoso.Chat", installer.ProductCode);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("1.2.3", arp.DisplayVersion);
    }

    // The portable twin ships the same branded app version strings but has no bootstrap
    // payload and no bootstrap branding — it must not be classified as an installer.
    [Fact]
    public void Portable_twin_without_payload_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(
            version: new VersionStrings(ProductName: "Contoso Chat", CompanyName: "Contoso Ltd", ProductVersion: "1.2.3"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new SquirrelProbe().Probe(peFile, stream));
    }

    // Electron apps ship Squirrel with the app payload (app.asar) and sometimes stray MSI
    // deployment files inside lib/ — the classification must stay a per-user EXE bootstrapper.
    [Fact]
    public void Electron_payload_stays_a_user_scope_exe()
    {
        byte[] nupkg = SquirrelFixtures.BuildNupkg(
            SquirrelFixtures.NuspecXml(id: "contoso-desktop", title: "Contoso Desktop"),
            "contoso-desktop.nuspec",
            ("lib/net45/resources/app.asar", Encoding.UTF8.GetBytes("asar-payload")),
            ("lib/net45/DeploymentTool.msi", Encoding.UTF8.GetBytes("not-a-real-msi")));
        byte[] setup = SquirrelFixtures.BuildClassicSetup(nupkg, nupkgName: "contoso-desktop-2.0.0-full.nupkg");

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal("Contoso Desktop", analysis.ProductName);
    }

    [Theory]
    [InlineData(Machine.I386, Architecture.X86)]
    [InlineData(Machine.Amd64, Architecture.X64)]
    [InlineData(Machine.Arm64, Architecture.Arm64)]
    public void Stub_machine_decides_architecture_when_the_package_name_is_silent(Machine machine, Architecture expected)
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()),
            machine: machine);

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Architecture_token_in_the_package_name_wins_over_the_stub()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()),
            nupkgName: "Contoso.Chat-1.2.3-arm64-full.nupkg",
            machine: Machine.I386);

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.Arm64, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Branded_stub_without_payload_still_claims_with_stub_metadata()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: SquirrelFixtures.BrandedStub);
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        InstallerAnalysis? analysis = new SquirrelProbe().Probe(peFile, stream);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Equal("Contoso Chat", analysis.ProductName);
    }

    [Fact]
    public void Corrupt_overlay_zip_with_branding_throws()
    {
        byte[] setup = AdvancedInstallerFixtures.Concat(
            PeFixtures.BuildExe(version: SquirrelFixtures.BrandedStub),
            CorruptZipOverlay());

        Assert.Throws<InvalidDataException>(() => Probe(setup));
    }

    [Fact]
    public void Corrupt_overlay_zip_without_branding_returns_null()
    {
        byte[] setup = AdvancedInstallerFixtures.Concat(PeFixtures.BuildExe(), CorruptZipOverlay());

        Assert.Null(Probe(setup));
    }

    [Fact]
    public void Overlay_zip_without_squirrel_payload_or_branding_returns_null()
    {
        byte[] setup = AdvancedInstallerFixtures.Concat(
            PeFixtures.BuildExe(),
            SquirrelFixtures.BuildStoredZip([("readme.txt", "hello"u8.ToArray())]));

        Assert.Null(Probe(setup));
    }

    [Fact]
    public void Nested_package_without_a_nuspec_throws()
    {
        byte[] nupkg = SquirrelFixtures.BuildStoredZip([("lib/net45/app.dll", new byte[16])]);
        byte[] setup = SquirrelFixtures.BuildClassicSetup(nupkg);

        Assert.Throws<InvalidDataException>(() => Probe(setup));
    }

    [Fact]
    public void Malformed_nuspec_xml_throws()
    {
        byte[] nupkg = SquirrelFixtures.BuildStoredZip(
            [("Contoso.Chat.nuspec", Encoding.UTF8.GetBytes("<package><metadata><id>Broken"))]);
        byte[] setup = SquirrelFixtures.BuildClassicSetup(nupkg);

        Assert.Throws<InvalidDataException>(() => Probe(setup));
    }

    [Fact]
    public void Nuspec_with_a_dtd_is_rejected()
    {
        string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE package [<!ENTITY x "boom">]>
            <package><metadata><id>&x;</id></metadata></package>
            """;
        byte[] nupkg = SquirrelFixtures.BuildStoredZip([("Contoso.Chat.nuspec", Encoding.UTF8.GetBytes(hostile))]);
        byte[] setup = SquirrelFixtures.BuildClassicSetup(nupkg);

        Assert.Throws<InvalidDataException>(() => Probe(setup));
    }

    [Fact]
    public void Oversized_declared_nupkg_degrades_to_stub_metadata_without_extraction()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()),
            version: SquirrelFixtures.BrandedStub,
            nupkgDeclaredSize: 512L * 1024 * 1024);

        InstallerAnalysis? analysis = Probe(setup);

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Equal("Contoso Chat", analysis.ProductName);
    }

    [Fact]
    public void The_stream_is_left_open_after_probing()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        new SquirrelProbe().Probe(peFile, stream);

        Assert.True(stream.CanRead);
    }

    /// <summary>A zip local-header signature followed by garbage instead of a parseable archive.</summary>
    private static byte[] CorruptZipOverlay()
    {
        byte[] overlay = new byte[128];
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }.CopyTo(overlay, 0);
        for (int i = 4; i < overlay.Length; i++)
        {
            overlay[i] = 0xFF;
        }

        return overlay;
    }

    private static InstallerAnalysis? Probe(byte[] setup)
    {
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new SquirrelProbe().Probe(peFile, stream);
    }
}
