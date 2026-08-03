using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Workflows.Mapping;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

public sealed class MappingEvidenceTests
{
    [Fact]
    public void Dependency_completeness_and_diagnostics_are_exposed_to_workflow_evidence()
    {
        var identity = new DownloadContentIdentity(new Sha256Hash(new string('A', 64)), 10);
        var content = new AssetContentEvidence(
            identity,
            "https://example.test/app.exe",
            "https://example.test/app.exe",
            "application/octet-stream",
            DateTimeOffset.UnixEpoch);
        var dependency = new PayloadDependencyAnalysis(
            [
                new DependencyEvidence
                {
                    PayloadPath = "app.exe",
                    Architecture = Architecture.X64,
                    Kind = DependencyEvidenceKind.VisualCppRuntime,
                    Status = DependencyEvidenceStatus.Unavailable,
                    Signals = ["scan-budget"],
                },
            ],
            [new AnalysisDiagnostic("DEP_SCAN_BUDGET", "Scanning reached its configured budget.", true)],
            isComplete: false);

        AssetAnalysisEvidence evidence = AssetAnalysisEvidence.FromAnalysis(
            new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                Installers = [new Installer { Architecture = Architecture.X64, InstallerType = InstallerType.Exe }],
            },
            content,
            dependency);

        Assert.False(evidence.DependencyAnalysisComplete);
        Assert.Contains(evidence.Diagnostics, value => value.StartsWith("DEP_SCAN_BUDGET:", StringComparison.Ordinal));
        Assert.Contains(evidence.Diagnostics, value => value.StartsWith("DEPENDENCY_ANALYSIS_INCOMPLETE:", StringComparison.Ordinal));
    }
}
