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
    public void Oversized_relevant_payload_is_rejected_by_the_configured_bound()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = 3,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(("app.exe", [1, 2, 3, 4]));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => analyzer.Analyze(archive, "large.zip"));

        Assert.Contains("per-payload analysis limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_entry_count_is_bounded_including_irrelevant_entries()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumArchiveEntries = 1,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("one.txt", [1]),
            ("two.txt", [2]));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => analyzer.Analyze(archive, "many.zip"));

        Assert.Contains("2 entries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Total_relevant_payload_bytes_are_bounded()
    {
        var analyzer = new PayloadDependencyAnalyzer(new PayloadDependencyAnalyzerOptions
        {
            MaximumPayloadBytes = 4,
            MaximumTotalPayloadBytes = 5,
        });
        using MemoryStream archive = DependencyFixtures.BuildZip(
            ("one.exe", [1, 2, 3]),
            ("two.dll", [4, 5, 6]));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => analyzer.Analyze(archive, "large.zip"));

        Assert.Contains("total analysis limit", exception.Message, StringComparison.Ordinal);
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
