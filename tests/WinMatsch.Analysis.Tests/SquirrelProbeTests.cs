using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Analysis.Pe;
using WinMatsch.Analysis.Squirrel;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class SquirrelProbeTests
{
    private static readonly byte[] _clowdSignature =
    [
        0x94, 0xF0, 0xB1, 0x7B, 0x68, 0x93, 0xE0, 0x29,
        0x37, 0xEB, 0x34, 0xEF, 0x53, 0xAA, 0xE7, 0xD4,
        0x2B, 0x54, 0xF5, 0x70, 0x7E, 0xF5, 0xD6, 0xF5,
        0x78, 0x54, 0x98, 0x3E, 0x5E, 0x94, 0xED, 0x7D,
    ];

    [Fact]
    public void Plain_pe_without_bundle_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);

        Assert.Null(new SquirrelProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Classic_setup_reads_named_data_resource_131()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(setup));

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
    }

    [Theory]
    [InlineData("RCDATA", 131)]
    [InlineData("DATA", 130)]
    public void Wrong_resource_type_or_id_is_not_classic_squirrel(string typeName, int resourceId)
    {
        byte[] payload = SquirrelFixtures.BuildStoredZip(
            [("app.nupkg", SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()))]);
        byte[] setup = SquirrelFixtures.BuildResourceSetup(payload, typeName, resourceId);

        Assert.Null(Probe(setup));
    }

    [Fact]
    public void Raw_overlay_zip_is_not_classic_squirrel()
    {
        byte[] overlay = SquirrelFixtures.BuildStoredZip(
            [("app.nupkg", SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()))]);
        byte[] setup = AdvancedInstallerFixtures.Concat(PeFixtures.BuildExe(), overlay);

        Assert.Null(Probe(setup));
    }

    [Fact]
    public void Clowd_setup_uses_the_bounded_in_image_bundle_locator()
    {
        byte[] setup = SquirrelFixtures.BuildClowdSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(setup));

        Assert.Equal(DetectedInstallerFormat.Squirrel, analysis.Format);
        Assert.Equal("Contoso.Chat", Assert.Single(analysis.Installers).ProductCode);
    }

    [Fact]
    public void Clowd_bundle_length_excludes_a_trailing_decoy_zip()
    {
        byte[] setup = SquirrelFixtures.BuildClowdSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));
        byte[] decoy = SquirrelFixtures.BuildNupkg(
            SquirrelFixtures.NuspecXml(id: "Decoy.App", title: "Decoy"));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(AdvancedInstallerFixtures.Concat(setup, decoy)));

        Assert.Equal("Contoso.Chat", Assert.Single(analysis.Installers).ProductCode);
    }

    [Fact]
    public void Clowd_locator_with_out_of_file_bounds_throws()
    {
        byte[] setup = SquirrelFixtures.BuildClowdSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));
        int signatureOffset = setup.AsSpan().IndexOf(_clowdSignature);
        BinaryPrimitives.WriteInt64LittleEndian(setup.AsSpan(signatureOffset - 8), long.MaxValue);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Probe(setup));
        Assert.Contains("bundle locator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_signature_only_in_overlay_is_ignored()
    {
        byte[] setup = AdvancedInstallerFixtures.Concat(
            PeFixtures.BuildExe(),
            new byte[16],
            _clowdSignature,
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));

        Assert.Null(Probe(setup));
    }

    [Fact]
    public void Portable_twin_without_bootstrap_evidence_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(
            version: new VersionStrings(ProductName: "Contoso Chat", CompanyName: "Contoso Ltd"));
        using var peFile = new PeFile(stream);

        Assert.Null(new SquirrelProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Electron_payload_stays_a_user_scope_exe()
    {
        byte[] nupkg = SquirrelFixtures.BuildNupkg(
            SquirrelFixtures.NuspecXml(id: "contoso-desktop", title: "Contoso Desktop"),
            "contoso-desktop.nuspec",
            ("lib/net45/resources/app.asar", Encoding.UTF8.GetBytes("asar-payload")),
            ("lib/net45/DeploymentTool.msi", Encoding.UTF8.GetBytes("not-a-real-msi")));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(SquirrelFixtures.BuildClassicSetup(nupkg, "contoso-desktop-2.0.0-full.nupkg")));

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.InstallerType);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal("Contoso Desktop", analysis.ProductName);
    }

    [Theory]
    [InlineData(Machine.I386)]
    [InlineData(Machine.Amd64)]
    [InlineData(Machine.Arm64)]
    public void Stub_machine_alone_does_not_decide_payload_architecture(Machine machine)
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()),
            machine: machine);

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(setup));
        Assert.Null(Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("SQUIRREL001", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
    }

    [Theory]
    [InlineData(Machine.I386, Architecture.X86)]
    [InlineData(Machine.Amd64, Architecture.X64)]
    [InlineData(Machine.Arm64, Architecture.Arm64)]
    public void Nupkg_payload_pe_decides_architecture(Machine machine, Architecture expected)
    {
        byte[] nupkg = SquirrelFixtures.BuildNupkg(
            SquirrelFixtures.NuspecXml(),
            extraEntries: [("lib/net45/app.exe", DependencyFixtures.BuildPe(machine))]);

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(SquirrelFixtures.BuildClassicSetup(nupkg)));

        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void Oversized_payload_architecture_is_sniffed_from_the_copied_prefix()
    {
        byte[] nupkg = BuildDeflatedNupkg(("lib/app/big.exe", OversizedPe(Machine.Amd64)));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(SquirrelFixtures.BuildClowdSetup(nupkg)));

        Assert.Equal(Architecture.X64, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("SQUIRREL002", diagnostic.Code);
        Assert.False(diagnostic.RequiresManualAnalysis);
        Assert.Contains("dependency evidence is incomplete", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_stubs_do_not_outvote_an_oversized_payload()
    {
        // The TableCloth shape: tiny x86 helper stubs plus an over-budget x64 app payload.
        byte[] nupkg = BuildDeflatedNupkg(
            ("lib/app/Squirrel.exe", DependencyFixtures.BuildPe(Machine.I386)),
            ("lib/app/App_ExecutionStub.exe", DependencyFixtures.BuildPe(Machine.I386)),
            ("lib/app/App.exe", OversizedPe(Machine.Amd64)));

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            Probe(SquirrelFixtures.BuildClowdSetup(nupkg)));

        Assert.Equal(Architecture.X64, Assert.Single(analysis.Installers).Architecture);
        Assert.Contains(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "SQUIRREL002" && !diagnostic.RequiresManualAnalysis);
        Assert.Contains(analysis.Diagnostics, static diagnostic => diagnostic.Code == "SQUIRREL001");
    }

    [Fact]
    public void Package_name_architecture_wins_over_stub()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()),
            "Contoso.Chat-1.2.3-arm64-full.nupkg",
            Machine.I386);

        Assert.Equal(Architecture.Arm64, Assert.Single(Probe(setup)!.Installers).Architecture);
    }

    [Fact]
    public void Branded_stub_without_payload_keeps_outer_only_classification()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: SquirrelFixtures.BrandedStub);
        using var peFile = new PeFile(stream);

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(
            new SquirrelProbe().Probe(peFile, stream));

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Null(installer.Architecture);
        Assert.Equal("Contoso Chat", analysis.ProductName);
        Assert.Equal("SQUIRREL001", Assert.Single(analysis.Diagnostics).Code);
    }

    [Fact]
    public void Corrupt_classic_resource_throws_explicitly()
    {
        byte[] setup = SquirrelFixtures.BuildResourceSetup(CorruptZip(), "DATA", 131);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Probe(setup));
        Assert.Contains("classic Squirrel payload resource", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_package_without_a_nuspec_throws()
    {
        byte[] nupkg = SquirrelFixtures.BuildStoredZip([("lib/net45/app.dll", new byte[16])]);

        Assert.Throws<InvalidDataException>(() => Probe(SquirrelFixtures.BuildClassicSetup(nupkg)));
    }

    [Fact]
    public void Malformed_nuspec_xml_throws()
    {
        byte[] nupkg = SquirrelFixtures.BuildStoredZip(
            [("Contoso.Chat.nuspec", Encoding.UTF8.GetBytes("<package><metadata><id>Broken"))]);

        Assert.Throws<InvalidDataException>(() => Probe(SquirrelFixtures.BuildClassicSetup(nupkg)));
    }

    [Fact]
    public void Nuspec_with_a_dtd_is_rejected()
    {
        string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE package [<!ENTITY x "boom">]>
            <package><metadata><id>&x;</id></metadata></package>
            """;
        byte[] nupkg = SquirrelFixtures.BuildStoredZip(
            [("Contoso.Chat.nuspec", Encoding.UTF8.GetBytes(hostile))]);

        Assert.Throws<InvalidDataException>(() => Probe(SquirrelFixtures.BuildClassicSetup(nupkg)));
    }

    [Fact]
    public void Outer_zip_entry_count_is_bounded_before_entries_materialize()
    {
        byte[] setup = SquirrelFixtures.BuildResourceSetup(
            DependencyFixtures.BuildZipWithEntryCount(AnalysisLimits.MaxArchiveEntries + 1).ToArray(),
            "DATA",
            131);

        AnalysisResourceLimitException error = Assert.Throws<AnalysisResourceLimitException>(
            () => Probe(setup));
        Assert.Contains(
            $"more than {AnalysisLimits.MaxArchiveEntries}",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_zip_central_directory_size_is_bounded_before_entries_materialize()
    {
        byte[] nupkg = DependencyFixtures.BuildZipWithEntryCount(
            entryCount: 300,
            entryNameLength: 60_000).ToArray();

        AnalysisResourceLimitException error = Assert.Throws<AnalysisResourceLimitException>(
            () => Probe(SquirrelFixtures.BuildClassicSetup(nupkg)));
        Assert.Contains("central directory larger", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stream_is_left_open_after_probing()
    {
        byte[] setup = SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml()));
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);

        new SquirrelProbe().Probe(peFile, stream);

        Assert.True(stream.CanRead);
    }

    private static byte[] CorruptZip()
    {
        byte[] data = new byte[128];
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }.CopyTo(data, 0);
        return data;
    }

    /// <summary>A valid PE padded with an overlay to just past the per-payload copy budget.</summary>
    private static byte[] OversizedPe(Machine machine)
    {
        byte[] pe = DependencyFixtures.BuildPe(machine);
        byte[] oversized = new byte[SquirrelProbe.MaxPayloadPeBytes + 4096];
        pe.CopyTo(oversized, 0);
        return oversized;
    }

    /// <summary>A nupkg with deflated entries, so oversized payloads stay small on disk.</summary>
    private static byte[] BuildDeflatedNupkg(params (string Name, byte[] Data)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Contoso.Chat.nuspec", Encoding.UTF8.GetBytes(SquirrelFixtures.NuspecXml()));
            foreach ((string name, byte[] data) in entries)
            {
                WriteEntry(zip, name, data);
            }
        }

        return buffer.ToArray();

        static void WriteEntry(ZipArchive zip, string name, byte[] data)
        {
            using Stream entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            entry.Write(data);
        }
    }

    private static InstallerAnalysis? Probe(byte[] setup)
    {
        using var stream = new MemoryStream(setup);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new SquirrelProbe().Probe(peFile, stream);
    }
}
