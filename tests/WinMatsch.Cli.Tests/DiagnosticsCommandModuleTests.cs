using System.Text.Json;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;
using WinMatsch.Workflows.Diagnostics;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class DiagnosticsCommandModuleTests
{
    [Fact]
    public async Task Help_lists_all_read_only_diagnostic_commands()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("analyze", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("validate", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("show", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("list-versions", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_json_contract_includes_analyzer_and_dependency_evidence()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(
            ["analyze", "fixture.exe", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            "{\"input\":\"fixture.exe\",\"fileName\":\"fixture.exe\",\"remote\":false,\"fromCache\":false,"
            + "\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\","
            + "\"sizeInBytes\":42,\"format\":\"portableExe\",\"confidence\":\"high\","
            + "\"product\":{\"name\":\"Fixture\",\"publisher\":\"Example\",\"version\":\"1.0\",\"copyright\":null},"
            + "\"installers\":[{\"architecture\":\"x64\",\"installerType\":\"portable\","
            + "\"nestedInstallerType\":null,\"productCode\":null,\"packageFamilyName\":null}],"
            + "\"dependencies\":[{\"payloadPath\":\"fixture.exe\",\"architecture\":\"x64\","
            + "\"kind\":\"dotNetRuntime\",\"status\":\"detected\",\"runtimeMajor\":10,"
            + "\"signals\":[\"runtimeconfig:framework=Microsoft.NETCore.App@10.0.0\"]}],"
            + "\"diagnostics\":[{\"code\":\"AN001\",\"message\":\"fixture diagnostic\","
            + "\"requiresManualAnalysis\":false}]}\n",
            result.StandardOutput);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "detected",
            document.RootElement.GetProperty("dependencies")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Analyze_wraps_remote_format_failures_as_operation_failures()
    {
        var analyzer = new FakeInstallerDiagnosticService
        {
            Failure = new FormatException("malformed remote metadata"),
        };
        CliHarness harness = CreateHarness(analyzer: analyzer);

        CliRunResult result = await harness.RunAsync(
            ["analyze", "https://example.test/setup.exe"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Installer analysis failed", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Configuration error", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_json_is_deterministic_and_blocking_findings_exit_nonzero()
    {
        var validation = new FakeManifestValidationService
        {
            Result = new ManifestValidationResult(
                NetworkValidationMode.Offline,
                WarningPolicy.Allow,
                [@"C:\fixture\Example.App.installer.yaml"],
                new ValidationReport(
                [
                    new ValidationFinding(
                        "VLD6001",
                        ValidationSeverity.Error,
                        "Origin SHA validation is unavailable.",
                        "https://example.test/app.exe"),
                ])),
        };
        CliHarness harness = CreateHarness(validation: validation);

        CliRunResult result = await harness.RunAsync(
            ["validate", "manifests", "--offline", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            "{\"isValid\":false,\"canProceed\":false,\"networkMode\":\"offline\","
            + "\"warningPolicy\":\"allow\",\"files\":[\"C:\\\\fixture\\\\Example.App.installer.yaml\"],"
            + "\"findings\":[{\"code\":\"VLD6001\",\"severity\":\"error\","
            + "\"message\":\"Origin SHA validation is unavailable.\","
            + "\"path\":\"https://example.test/app.exe\"}]}\n",
            result.StandardOutput);
        Assert.NotNull(validation.LastRequest);
        Assert.True(validation.LastRequest!.Offline);
    }

    [Fact]
    public async Task Show_raw_json_preserves_repository_content_contract()
    {
        var repository = new FakeRepositoryDiagnosticService();
        CliHarness harness = CreateHarness(repository: repository);
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token";

        CliRunResult result = await harness.RunAsync(
            ["show", "Example.App", "2.0.0", "--raw", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            "{\"repository\":\"microsoft/winget-pkgs\",\"reference\":\"main\","
            + "\"packageIdentifier\":\"Example.App\",\"packageVersion\":\"2.0.0\","
            + "\"normalized\":false,\"files\":[{\"path\":\"manifests/e/Example/App/2.0.0/Example.App.yaml\","
            + "\"content\":\"PackageIdentifier: Example.App\\n\"}]}\n",
            result.StandardOutput);
        Assert.False(repository.LastNormalize);
    }

    [Fact]
    public async Task List_versions_text_preserves_service_order_and_page_options()
    {
        var repository = new FakeRepositoryDiagnosticService();
        CliHarness harness = CreateHarness(repository: repository);
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token";

        CliRunResult result = await harness.RunAsync(
            ["list-versions", "Example.App", "--skip", "1", "--limit", "2"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("2.0.0\n2.0.0-rc\n", result.StandardOutput);
        Assert.Equal(1, repository.LastSkip);
        Assert.Equal(2, repository.LastLimit);
    }

    [Fact]
    public async Task Cancellation_maps_to_cancelled_without_stack_trace()
    {
        var analyzer = new FakeInstallerDiagnosticService
        {
            Failure = new OperationCanceledException(),
        };
        CliHarness harness = CreateHarness(analyzer: analyzer);

        CliRunResult result = await harness.RunAsync(["analyze", "fixture.exe"]);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    private static CliHarness CreateHarness(
        FakeInstallerDiagnosticService? analyzer = null,
        FakeManifestValidationService? validation = null,
        FakeRepositoryDiagnosticService? repository = null)
    {
        analyzer ??= new FakeInstallerDiagnosticService();
        validation ??= new FakeManifestValidationService();
        repository ??= new FakeRepositoryDiagnosticService();
        var harness = new CliHarness();
        harness.Modules.Add(new DiagnosticsCommandModule(
            analyzer,
            validation,
            _ => repository));
        return harness;
    }
}

internal sealed class FakeInstallerDiagnosticService : IInstallerDiagnosticService
{
    public Exception? Failure { get; init; }

    public Task<InstallerDiagnosticResult> AnalyzeAsync(
        InstallerAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<InstallerDiagnosticResult>(Failure);
        }

        return Task.FromResult(new InstallerDiagnosticResult(
            request.Input,
            "fixture.exe",
            IsRemote: false,
            IsFromCache: false,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            42,
            "high",
            new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.PortableExe,
                ProductName = "Fixture",
                Publisher = "Example",
                ProductVersion = "1.0",
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Portable,
                    },
                ],
                Diagnostics = [new AnalysisDiagnostic("AN001", "fixture diagnostic")],
            },
            new PayloadDependencyAnalysis(
            [
                new DependencyEvidence
                {
                    PayloadPath = "fixture.exe",
                    Architecture = Architecture.X64,
                    Kind = DependencyEvidenceKind.DotNetRuntime,
                    Status = DependencyEvidenceStatus.Detected,
                    RuntimeMajor = 10,
                    Signals = ["runtimeconfig:framework=Microsoft.NETCore.App@10.0.0"],
                },
            ])));
    }
}

internal sealed class FakeManifestValidationService : IManifestValidationService
{
    public ManifestValidationResult Result { get; init; } = new(
        NetworkValidationMode.Online,
        WarningPolicy.Allow,
        [],
        new ValidationReport());

    public ManifestValidationRequest? LastRequest { get; private set; }

    public Task<ManifestValidationResult> ValidateAsync(
        ManifestValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeRepositoryDiagnosticService : IRepositoryDiagnosticService
{
    public bool LastNormalize { get; private set; }

    public int LastSkip { get; private set; }

    public int LastLimit { get; private set; }

    public Task<PackageVersionResult> GetPackageVersionAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        PackageVersion version,
        bool normalize,
        CancellationToken cancellationToken = default)
    {
        LastNormalize = normalize;
        return Task.FromResult(new PackageVersionResult(
            repository,
            "main",
            identifier,
            version,
            normalize,
            [
                new RepositoryManifestFile(
                    "manifests/e/Example/App/2.0.0/Example.App.yaml",
                    "PackageIdentifier: Example.App\n"),
            ]));
    }

    public Task<PackageVersionsResult> ListVersionsAsync(
        RepositoryCoordinates repository,
        PackageIdentifier identifier,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        LastSkip = skip;
        LastLimit = limit;
        return Task.FromResult(new PackageVersionsResult(
            repository,
            "main",
            identifier,
            skip,
            limit,
            4,
            [new PackageVersion("2.0.0"), new PackageVersion("2.0.0-rc")]));
    }
}
