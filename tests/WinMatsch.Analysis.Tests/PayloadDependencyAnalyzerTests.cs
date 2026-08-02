using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class PayloadDependencyAnalyzerTests
{
    private readonly PayloadDependencyAnalyzer _analyzer = new();

    [Fact]
    public void Analysis_and_signal_collections_are_defensively_copied_and_read_only()
    {
        string[] sourceSignals = ["vcruntime140.dll"];
        var evidence = new DependencyEvidence
        {
            PayloadPath = "app.exe",
            Kind = DependencyEvidenceKind.VisualCppRuntime,
            Status = DependencyEvidenceStatus.Detected,
            Signals = sourceSignals,
        };
        DependencyEvidence[] sourceEvidence = [evidence];
        var analysis = new PayloadDependencyAnalysis(sourceEvidence);

        sourceSignals[0] = "changed.dll";
        sourceEvidence[0] = new DependencyEvidence
        {
            PayloadPath = "other.exe",
            Kind = DependencyEvidenceKind.DotNetRuntime,
            Status = DependencyEvidenceStatus.Absent,
        };

        Assert.Equal("vcruntime140.dll", Assert.Single(evidence.Signals));
        Assert.Same(evidence, Assert.Single(analysis.Evidence));
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)evidence.Signals).Add("another.dll"));
        Assert.Throws<NotSupportedException>(
            () => ((IList<DependencyEvidence>)analysis.Evidence).Clear());
        Assert.True(analysis.IsComplete);
    }

    [Theory]
    [InlineData(Machine.I386, Architecture.X86)]
    [InlineData(Machine.Amd64, Architecture.X64)]
    public void Vc_runtime_import_is_detected_for_the_pe_architecture(
        Machine machine,
        Architecture architecture)
    {
        using var payload = new MemoryStream(DependencyFixtures.BuildPe(machine, "KERNEL32.dll", "VCRUNTIME140.dll"));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(payload, "setup.exe");

        DependencyEvidence evidence = Find(analysis, "setup.exe", DependencyEvidenceKind.VisualCppRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.Equal(architecture, evidence.Architecture);
        Assert.Equal(["vcruntime140.dll"], evidence.Signals);
    }

    [Theory]
    [InlineData("8.0.17", 8)]
    [InlineData("9.0.6", 9)]
    public void Runtimeconfig_framework_version_detects_the_dotnet_runtime_major(
        string version,
        int expectedMajor)
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("bin/app.exe", DependencyFixtures.BuildPe(Machine.Amd64)),
            ("bin/app.runtimeconfig.json", DependencyFixtures.RuntimeConfig(version)));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "app.zip");

        DependencyEvidence evidence = Find(analysis, "bin/app.exe", DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.Equal(expectedMajor, evidence.RuntimeMajor);
        Assert.Equal(Architecture.X64, evidence.Architecture);
        Assert.Contains($"runtimeconfig:framework=Microsoft.NETCore.App@{version}", evidence.Signals);
    }

    [Fact]
    public void Tfm_without_a_framework_version_is_inferred_not_detected()
    {
        byte[] runtimeConfig = """{"runtimeOptions":{"tfm":"net10.0-windows"}}"""u8.ToArray();
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("tool.exe", DependencyFixtures.BuildPe(Machine.I386)),
            ("tool.runtimeconfig.json", runtimeConfig));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "tool.zip");

        DependencyEvidence evidence = Find(analysis, "tool.exe", DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Inferred, evidence.Status);
        Assert.Equal(10, evidence.RuntimeMajor);
        Assert.Equal(Architecture.X86, evidence.Architecture);
    }

    [Fact]
    public void Legacy_dotnet_framework_tfm_is_not_inferred_as_a_runtime_major()
    {
        byte[] runtimeConfig = """{"runtimeOptions":{"tfm":"net48"}}"""u8.ToArray();
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("tool.exe", DependencyFixtures.BuildPe(Machine.I386)),
            ("tool.runtimeconfig.json", runtimeConfig));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "tool.zip");

        DependencyEvidence evidence = Find(analysis, "tool.exe", DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Absent, evidence.Status);
        Assert.Null(evidence.RuntimeMajor);
    }

    [Fact]
    public void Malformed_runtimeconfig_is_ambiguous_and_retains_payload_architecture()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)),
            ("app.runtimeconfig.json", "{"u8.ToArray()));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "broken.zip");

        DependencyEvidence evidence = Find(analysis, "app.exe", DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        Assert.Equal(Architecture.X64, evidence.Architecture);
        Assert.Contains("runtimeconfig:malformed-json", evidence.Signals);
    }

    [Fact]
    public void Mixed_archive_keeps_runtime_and_vc_evidence_on_the_correct_payload()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("x86/alpha.exe", DependencyFixtures.BuildPe(Machine.I386, "MSVCP140.dll")),
            ("x86/alpha.runtimeconfig.json", DependencyFixtures.RuntimeConfig("8.0.4")),
            ("x64/beta.exe", DependencyFixtures.BuildPe(Machine.Amd64, "KERNEL32.dll")),
            ("x64/beta.runtimeconfig.json", DependencyFixtures.RuntimeConfig("9.0.1")));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "mixed.zip");

        DependencyEvidence alphaVc = Find(analysis, "x86/alpha.exe", DependencyEvidenceKind.VisualCppRuntime);
        DependencyEvidence alphaDotNet = Find(analysis, "x86/alpha.exe", DependencyEvidenceKind.DotNetRuntime);
        DependencyEvidence betaVc = Find(analysis, "x64/beta.exe", DependencyEvidenceKind.VisualCppRuntime);
        DependencyEvidence betaDotNet = Find(analysis, "x64/beta.exe", DependencyEvidenceKind.DotNetRuntime);

        Assert.Equal((DependencyEvidenceStatus.Detected, Architecture.X86), (alphaVc.Status, alphaVc.Architecture));
        Assert.Equal((DependencyEvidenceStatus.Detected, 8), (alphaDotNet.Status, alphaDotNet.RuntimeMajor));
        Assert.Equal((DependencyEvidenceStatus.Absent, Architecture.X64), (betaVc.Status, betaVc.Architecture));
        Assert.Equal((DependencyEvidenceStatus.Detected, 9), (betaDotNet.Status, betaDotNet.RuntimeMajor));
    }

    [Fact]
    public void Bundled_hostfxr_is_ambiguous_instead_of_a_mandatory_runtime_detection()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("host/fxr/9.0.2/hostfxr.dll", DependencyFixtures.BuildPe(Machine.Amd64)));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "self-contained.zip");

        DependencyEvidence evidence = Find(
            analysis,
            "host/fxr/9.0.2/hostfxr.dll",
            DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        Assert.Equal(9, evidence.RuntimeMajor);
        Assert.Equal(Architecture.X64, evidence.Architecture);
    }

    [Fact]
    public void Included_frameworks_without_shared_framework_requirement_is_absent_evidence()
    {
        byte[] runtimeConfig = Encoding.UTF8.GetBytes(
            """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.0"}]}}""");
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)),
            ("app.runtimeconfig.json", runtimeConfig),
            ("hostfxr.dll", DependencyFixtures.BuildPe(Machine.Amd64)));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "self-contained.zip");

        DependencyEvidence evidence = Find(analysis, "app.exe", DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Absent, evidence.Status);
        Assert.Null(evidence.RuntimeMajor);
        Assert.Contains("runtimeconfig:no-shared-framework", evidence.Signals);
        Assert.Contains("bundled-hostfxr:hostfxr.dll", evidence.Signals);
    }

    [Fact]
    public void Valid_pe_without_runtime_signals_reports_absent_controls()
    {
        using var payload = new MemoryStream(DependencyFixtures.BuildPe(Machine.I386, "KERNEL32.dll"));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(payload, "plain.exe");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal("plain.exe", evidence.PayloadPath);
            Assert.Equal(Architecture.X86, evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Absent, evidence.Status);
        });
    }

    [Fact]
    public void Truncated_bsjb_metadata_root_cannot_create_neutral_evidence()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.I386);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(peOffset + 20));
        int sectionOffset = optionalOffset + optionalSize;
        uint sectionRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 12));
        int sectionRawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 20)));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 208), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 212), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 8), sectionRva + 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 12), 17);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 72), 0x424A5342);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 84), 1);
        pe[sectionRawOffset + 88] = 0;
        using MemoryStream archive = DependencyFixtures.BuildZip(("forged.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "forged.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal(Architecture.X86, evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Self_referential_metadata_stream_cannot_create_neutral_evidence()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.I386);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(peOffset + 20));
        int sectionOffset = optionalOffset + optionalSize;
        uint sectionRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 12));
        int sectionRawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 20)));
        int metadataOffset = sectionRawOffset + 72;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 208), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 212), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 8), sectionRva + 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 12), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(metadataOffset), 0x424A5342);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(metadataOffset + 12), 4);
        "v4\0\0"u8.CopyTo(pe.AsSpan(metadataOffset + 16));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(metadataOffset + 22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(metadataOffset + 24), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(metadataOffset + 28), 4);
        "#~\0\0"u8.CopyTo(pe.AsSpan(metadataOffset + 32));
        using MemoryStream archive = DependencyFixtures.BuildZip(("forged.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "forged.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal(Architecture.X86, evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Forged_clr_header_without_metadata_signature_cannot_create_neutral_evidence()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.I386);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(peOffset + 20));
        int sectionOffset = optionalOffset + optionalSize;
        uint sectionRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 12));
        int sectionRawOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 20)));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 208), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 212), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 8), sectionRva + 72);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(sectionRawOffset + 16), 1);
        using MemoryStream archive = DependencyFixtures.BuildZip(("forged.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "forged.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal(Architecture.X86, evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Valid_pe_with_no_data_directories_reports_absent_controls()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.Amd64);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(peOffset + 24 + 108), 0);
        using MemoryStream archive = DependencyFixtures.BuildZip(("minimal.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "minimal.zip");

        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Absent, evidence.Status));
    }

    [Fact]
    public void Truncated_clr_header_cannot_create_false_neutral_evidence()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.I386);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(peOffset + 20));
        int sectionOffset = optionalOffset + optionalSize;
        uint sectionRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 12));
        uint sectionRawOffset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(sectionOffset + 20));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 208), sectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 212), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(checked((int)sectionRawOffset) + 16), 1);
        using MemoryStream archive = DependencyFixtures.BuildZip(("forged.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "forged.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal(Architecture.X86, evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Half_populated_import_directory_is_ambiguous_not_absent()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.Amd64);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 120), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 124), 20);
        using MemoryStream archive = DependencyFixtures.BuildZip(("malformed.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "malformed.zip");

        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status));
    }

    [Fact]
    public void Data_directory_count_beyond_optional_header_is_ambiguous()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.Amd64);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int optionalOffset = peOffset + 24;
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(peOffset + 20));
        int capacity = (optionalSize - 112) / 8;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optionalOffset + 108), checked((uint)capacity + 1));
        using MemoryStream archive = DependencyFixtures.BuildZip(("malformed.exe", pe));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "malformed.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Null(evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Unavailable_runtimeconfig_prevents_contradictory_dotnet_absent_evidence()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumRuntimeConfigBytes = 4,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)),
            ("app.runtimeconfig.json", DependencyFixtures.RuntimeConfig("8.0.0")));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "runtime.zip");

        DependencyEvidence evidence = Find(
            analysis,
            "app.exe",
            DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains("runtimeconfig:analysis-unavailable", evidence.Signals);
    }

    [Fact]
    public void Unavailable_nearby_hostfxr_prevents_contradictory_dotnet_absent_evidence()
    {
        byte[] app = DependencyFixtures.BuildPe(Machine.Amd64);
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = app.Length,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("bin/app.exe", app),
            ("bin/hostfxr.dll", [.. app, 0]));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "runtime.zip");

        DependencyEvidence evidence = Find(
            analysis,
            "bin/app.exe",
            DependencyEvidenceKind.DotNetRuntime);
        Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains("hostfxr:analysis-unavailable", evidence.Signals);
    }

    [Fact]
    public void Malformed_pe_metadata_is_ambiguous_not_absent()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(("broken.exe", [1, 2, 3, 4]));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "broken.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal("broken.exe", evidence.PayloadPath);
            Assert.Null(evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Plausible_but_structurally_invalid_pe_header_is_not_absent_evidence()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("malformed.exe", DependencyFixtures.BuildStructurallyInvalidPeHeader()));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "malformed.zip");

        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Null(evidence.Architecture);
            Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        });
    }

    [Fact]
    public void Unassociated_runtimeconfig_does_not_invent_an_architecture()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("orphan.runtimeconfig.json", DependencyFixtures.RuntimeConfig("8.0.0")));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "metadata.zip");

        DependencyEvidence evidence = Assert.Single(analysis.Evidence);
        Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
        Assert.Equal(8, evidence.RuntimeMajor);
        Assert.Null(evidence.Architecture);
    }

    [Fact]
    public void Oversized_relevant_payload_returns_unavailable_evidence_without_aborting()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = 3,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(("app.exe", [1, 2, 3, 4]));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "large.zip");

        Assert.False(analysis.IsComplete);
        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal("app.exe", evidence.PayloadPath);
            Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status);
            Assert.Contains("analysis-unavailable:payload-byte-budget", evidence.Signals);
        });
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP003");
    }

    [Fact]
    public void Seventy_megabyte_compressed_entry_does_not_abort_archive_analysis()
    {
        using MemoryStream archive = DependencyFixtures.BuildCompressedZeroZip(
            "electron/app.exe",
            70L * 1024 * 1024);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "electron.zip");

        Assert.False(analysis.IsComplete);
        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP003");
    }

    [Fact]
    public void Inflated_declared_length_does_not_consume_the_actual_byte_budget()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll");
        using var archive = new MemoryStream(SquirrelFixtures.BuildStoredZip(
            [("app.exe", pe)],
            declaredSizeOverrideForLastEntry: 200L * 1024 * 1024));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "declared-size.zip");

        DependencyEvidence evidence = Find(
            analysis,
            "app.exe",
            DependencyEvidenceKind.VisualCppRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void Archive_at_dependency_entry_limit_is_complete()
    {
        using MemoryStream archive = DependencyFixtures.BuildZipWithEntryCount(
            PayloadDependencyAnalyzerOptions.DefaultMaximumArchiveEntries);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "boundary.zip");

        Assert.True(analysis.IsComplete);
        Assert.Empty(analysis.Evidence);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP003");
    }

    [Theory]
    [InlineData(PayloadDependencyAnalyzerOptions.DefaultMaximumArchiveEntries + 1)]
    [InlineData(AnalysisLimits.MaxArchiveEntries)]
    [InlineData(AnalysisLimits.MaxArchiveEntries + 1)]
    public void Archive_over_dependency_entry_limit_returns_unavailable_evidence(int entryCount)
    {
        using MemoryStream archive = DependencyFixtures.BuildZipWithEntryCount(entryCount);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, $"{entryCount}.zip");

        Assert.False(analysis.IsComplete);
        Assert.All(analysis.Evidence, evidence =>
        {
            Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status);
            Assert.Contains("analysis-unavailable:resource-limit", evidence.Signals);
        });
        AnalysisDiagnostic diagnostic = Assert.Single(
            analysis.Diagnostics,
            item => item.Code == "DEP003");
        Assert.Contains(
            PayloadDependencyAnalyzerOptions.DefaultMaximumArchiveEntries.ToString(),
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileAnalyzer_accepts_its_maximum_while_dependency_evidence_degrades()
    {
        using MemoryStream archive = DependencyFixtures.BuildZipWithEntryCount(
            AnalysisLimits.MaxArchiveEntries,
            ("bin/app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));

        InstallerAnalysis installer = FileAnalyzer.Analyze(archive, "maximum.zip");
        archive.Position = 0;
        PayloadDependencyAnalysis dependencies = _analyzer.Analyze(archive, "maximum.zip");

        Assert.Equal(DetectedInstallerFormat.Zip, installer.Format);
        Assert.False(dependencies.IsComplete);
        Assert.All(
            dependencies.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
    }

    [Fact]
    public void FileAnalyzer_rejects_over_maximum_but_dependency_analysis_still_degrades()
    {
        using MemoryStream archive = DependencyFixtures.BuildZipWithEntryCount(
            AnalysisLimits.MaxArchiveEntries + 1,
            ("bin/app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));

        Assert.Throws<AnalysisResourceLimitException>(
            () => FileAnalyzer.Analyze(archive, "over-maximum.zip"));
        archive.Position = 0;

        PayloadDependencyAnalysis dependencies = _analyzer.Analyze(archive, "over-maximum.zip");

        Assert.False(dependencies.IsComplete);
        Assert.All(
            dependencies.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
    }

    [Fact]
    public void Central_directory_size_is_bounded_before_zip_entries_materialize()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumCentralDirectoryBytes = 1024,
        });
        using MemoryStream archive = DependencyFixtures.BuildZipWithEntryCount(20);

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "directory-bomb.zip");

        Assert.False(analysis.IsComplete);
        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == "DEP003"
                && diagnostic.Message.Contains("central directory larger", StringComparison.Ordinal));
    }

    [Fact]
    public void Impossible_central_directory_declaration_remains_corrupt()
    {
        using var archive = new MemoryStream(
            SquirrelFixtures.BuildDirectoryBomb(entryCount: 1, centralDirectorySize: 2048));

        Assert.Throws<InvalidDataException>(
            () => _analyzer.Analyze(archive, "invalid-directory.zip"));
    }

    [Fact]
    public void Underreported_central_directory_size_is_rejected_before_materialization()
    {
        using MemoryStream valid = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));
        byte[] archiveBytes = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(archiveBytes.AsSpan(archiveBytes.Length - 10), 1);
        using var archive = new MemoryStream(archiveBytes);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => _analyzer.Analyze(archive, "underreported.zip"));

        Assert.Contains("truncated or has an invalid ZIP directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_archive_is_not_converted_to_unavailable_evidence()
    {
        using MemoryStream valid = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));
        byte[] archiveBytes = valid.ToArray();
        using var truncated = new MemoryStream(archiveBytes[..^1]);

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(truncated, "truncated.zip"));
    }

    [Fact]
    public void Bounded_central_directory_digital_signature_is_accepted()
    {
        using MemoryStream valid = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll")));
        byte[] archiveBytes = DependencyFixtures.AddCentralDirectoryDigitalSignature(
            valid.ToArray(),
            "signed"u8);
        using var archive = new MemoryStream(archiveBytes);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "signed.zip");

        Assert.Equal(
            DependencyEvidenceStatus.Detected,
            Find(analysis, "app.exe", DependencyEvidenceKind.VisualCppRuntime).Status);
    }

    [Fact]
    public void Bounded_archive_extra_data_before_file_headers_is_accepted()
    {
        using MemoryStream valid = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll")));
        byte[] archiveBytes = DependencyFixtures.AddArchiveExtraDataRecord(
            valid.ToArray(),
            "extra"u8);
        using var archive = new MemoryStream(archiveBytes);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(archive, "extra.zip");

        Assert.Equal(
            DependencyEvidenceStatus.Detected,
            Find(analysis, "app.exe", DependencyEvidenceKind.VisualCppRuntime).Status);
    }

    [Fact]
    public void Non_seekable_zip_returns_unavailable_instead_of_leaking_position_errors()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));
        using Stream nonSeekable = DependencyFixtures.AsNonSeekable(archive);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(nonSeekable, "stream.zip");

        Assert.False(analysis.IsComplete);
        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP001");
    }

    [Fact]
    public void Total_relevant_payload_budget_counts_actual_reads_and_degrades()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = 4,
            MaximumTotalPayloadBytes = 5,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("one.exe", [1, 2, 3]),
            ("two.dll", [4, 5, 6]));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "large.zip");

        Assert.False(analysis.IsComplete);
        Assert.Contains(
            analysis.Evidence,
            evidence => evidence.PayloadPath == "two.dll"
                && evidence.Status == DependencyEvidenceStatus.Unavailable
                && evidence.Signals.Contains("analysis-unavailable:aggregate-byte-budget"));
    }

    [Fact]
    public void Archive_read_operation_budget_bounds_decompression_work()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumArchiveReadOperations = 1,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "work.zip");

        Assert.False(analysis.IsComplete);
        Assert.Contains(
            analysis.Evidence,
            evidence => evidence.Status == DependencyEvidenceStatus.Unavailable
                && evidence.Signals.Contains("analysis-unavailable:work-budget"));
    }

    [Fact]
    public void Compressed_byte_budget_bounds_empty_deflate_block_cpu_amplification()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumCompressedPayloadBytes = 1024,
            MaximumTotalCompressedBytes = 1024,
        });
        using MemoryStream archive = DependencyFixtures.BuildDeflateWorkAmplificationZip(
            "amplified.exe",
            emptyBlockCount: 20_000,
            payload: (byte)'M');

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "amplified.zip");

        Assert.False(analysis.IsComplete);
        Assert.Contains(
            analysis.Evidence,
            evidence => evidence.Status == DependencyEvidenceStatus.Unavailable
                && evidence.Signals.Contains("analysis-unavailable:compressed-byte-budget"));
    }

    [Fact]
    public void Payload_exactly_equal_to_aggregate_budget_is_complete()
    {
        byte[] pe = DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll");
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = pe.Length,
            MaximumTotalPayloadBytes = pe.Length,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(("app.exe", pe));

        PayloadDependencyAnalysis analysis = analyzer.Analyze(archive, "exact.zip");

        Assert.True(analysis.IsComplete);
        Assert.Equal(
            DependencyEvidenceStatus.Detected,
            Find(analysis, "app.exe", DependencyEvidenceKind.VisualCppRuntime).Status);
    }

    [Fact]
    public void Two_hundred_megabyte_sparse_executable_uses_targeted_pe_reads()
    {
        using DependencyFixtures.SparsePrefixStream stream = DependencyFixtures.BuildSparsePeStream(
            Machine.Amd64,
            200L * 1024 * 1024,
            "VCRUNTIME140.dll");

        PayloadDependencyAnalysis dependencies = _analyzer.Analyze(stream, "large-tool.exe");

        DependencyEvidence evidence = Find(
            dependencies,
            "large-tool.exe",
            DependencyEvidenceKind.VisualCppRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.Equal(Architecture.X64, evidence.Architecture);
        Assert.True(stream.TotalBytesRead < 32L * 1024 * 1024);

        stream.Position = 0;
        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "large-tool.exe");
        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
    }

    [Fact]
    public void Wrapper_stub_without_payload_signal_is_ambiguous_not_absent()
    {
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
        });
        using var stream = new MemoryStream(installer);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "setup.exe");

        Assert.All(
            analysis.Evidence.Where(evidence => evidence.PayloadPath == "setup.exe"),
            evidence =>
            {
                Assert.Equal(DependencyEvidenceStatus.Ambiguous, evidence.Status);
                Assert.Contains(evidence.Signals, signal => signal.StartsWith("outer-stub-only:", StringComparison.Ordinal));
            });
    }

    [Fact]
    public void Inno_embedded_pe_supplies_inner_vc_runtime_evidence()
    {
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            AdditionalPayloadBytes = DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll"),
        });
        using var stream = new MemoryStream(installer);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "setup.exe");

        DependencyEvidence evidence = Assert.Single(
            analysis.Evidence,
            item => item.PayloadPath.StartsWith("inno-payload/", StringComparison.Ordinal)
                && item.Kind == DependencyEvidenceKind.VisualCppRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.Equal(Architecture.X64, evidence.Architecture);
        Assert.Contains("vcruntime140.dll", evidence.Signals);
    }

    [Fact]
    public void Advanced_installer_nested_resource_limit_returns_unavailable_evidence()
    {
        byte[] msi = MsiFixtures.BuildMsi([]);
        string encodedName = MsiFixtures.EncodeStreamName("Property", isTable: true);
        int directoryEntry = msi.AsSpan().IndexOf(Encoding.Unicode.GetBytes(encodedName + "\0"));
        Assert.True(directoryEntry >= 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            msi.AsSpan(directoryEntry + 120),
            (ulong)AnalysisLimits.MaxMsiStreamBytes + 1);
        byte[] setup = AdvancedInstallerFixtures.BuildContainer(
        [
            new AdvancedInstallerFixtures.FixtureEntry(1, 0, 0, "payload.bin", msi),
        ]);
        using var stream = new MemoryStream(setup);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "setup.exe");

        Assert.False(analysis.IsComplete);
        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP003");
    }

    [Fact]
    public void Squirrel_nested_resource_limit_returns_unavailable_evidence()
    {
        byte[] nupkg = SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml());
        byte[] releaseZip = SquirrelFixtures.BuildStoredZip(
        [
            ("RELEASES", "stub"u8.ToArray()),
            ("Contoso.Chat-1.2.3-full.nupkg", nupkg),
        ],
        declaredSizeOverrideForLastEntry: 300L * 1024 * 1024);
        byte[] setup = SquirrelFixtures.BuildResourceSetup(releaseZip, "DATA", 131);
        using var stream = new MemoryStream(setup);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "Setup.exe");

        Assert.False(analysis.IsComplete);
        Assert.All(
            analysis.Evidence,
            evidence => Assert.Equal(DependencyEvidenceStatus.Unavailable, evidence.Status));
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "DEP003");
    }

    [Fact]
    public void Inno_payload_candidate_exhaustion_marks_dependency_analysis_incomplete()
    {
        byte[][] payloads =
        [
            .. Enumerable.Range(0, 65)
                .Select(_ => DependencyFixtures.BuildPe(Machine.Amd64)),
        ];
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            AdditionalPayloadBytes = AdvancedInstallerFixtures.Concat(payloads),
        });
        using var stream = new MemoryStream(installer);

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "setup.exe");

        Assert.False(analysis.IsComplete);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "INNO013");
    }

    [Fact]
    public void Squirrel_nupkg_pe_supplies_inner_vc_runtime_evidence()
    {
        byte[] nupkg = SquirrelFixtures.BuildNupkg(
            SquirrelFixtures.NuspecXml(),
            extraEntries:
            [
                ("lib/net45/app.exe", DependencyFixtures.BuildPe(Machine.Amd64, "VCRUNTIME140.dll")),
            ]);
        using var stream = new MemoryStream(SquirrelFixtures.BuildClassicSetup(nupkg));

        PayloadDependencyAnalysis analysis = _analyzer.Analyze(stream, "Setup.exe");

        DependencyEvidence evidence = Find(
            analysis,
            "squirrel-nupkg/lib/net45/app.exe",
            DependencyEvidenceKind.VisualCppRuntime);
        Assert.Equal(DependencyEvidenceStatus.Detected, evidence.Status);
        Assert.Equal(Architecture.X64, evidence.Architecture);
    }

    [Fact]
    public void Cancellation_is_not_converted_to_unavailable_evidence()
    {
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("app.exe", DependencyFixtures.BuildPe(Machine.Amd64)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => _analyzer.AnalyzeWithCancellation(archive, "cancelled.zip", cancellation.Token));
    }

    [Theory]
    [InlineData("../app.exe")]
    [InlineData("/root/app.exe")]
    [InlineData(@"C:\payload\app.exe")]
    [InlineData("C:/payload/app.exe")]
    public void Parent_and_absolute_archive_paths_are_rejected(string path)
    {
        using MemoryStream archive = DependencyFixtures.BuildZip((path, [1]));

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(archive, "hostile.zip"));
    }

    [Fact]
    public void Zero_and_negative_options_are_rejected()
    {
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumArchiveEntries = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumArchiveEntries = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumPayloadBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumPayloadBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumTotalPayloadBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumTotalPayloadBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumCompressedPayloadBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumCompressedPayloadBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumTotalCompressedBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumTotalCompressedBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumCentralDirectoryBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumCentralDirectoryBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumArchiveReadOperations = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumArchiveReadOperations = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumRuntimeConfigBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumRuntimeConfigBytes = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumImportDescriptors = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumImportDescriptors = -1 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumImportNameBytes = 0 });
        AssertInvalid(new PayloadDependencyAnalyzerOptions { MaximumImportNameBytes = -1 });
    }

    private static DependencyEvidence Find(
        PayloadDependencyAnalysis analysis,
        string payloadPath,
        DependencyEvidenceKind kind)
        => Assert.Single(
            analysis.Evidence,
            evidence => evidence.PayloadPath == payloadPath && evidence.Kind == kind);

    private static void AssertInvalid(PayloadDependencyAnalyzerOptions options)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PayloadDependencyAnalyzer(options));
}
