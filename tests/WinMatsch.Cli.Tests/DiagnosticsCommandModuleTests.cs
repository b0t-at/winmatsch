using System.Text.Json;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Cli.Commands.Diagnostics;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
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
        Assert.Contains(
            "Downloading and analyzing installer",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"input\":\"fixture.exe\",\"fileName\":\"fixture.exe\","
            + "\"remote\":false,\"fromCache\":false,"
            + "\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\","
            + "\"sizeInBytes\":42,\"format\":\"portableExe\",\"formatCode\":\"portableExe\","
            + "\"confidence\":\"high\","
            + "\"product\":{\"name\":\"Fixture\",\"publisher\":\"Example\",\"version\":\"1.0\",\"copyright\":null},"
            + "\"installers\":[{\"architecture\":\"x64\",\"architectureCode\":\"x64\","
            + "\"installerType\":\"portable\",\"installerTypeCode\":\"portable\","
            + "\"nestedInstallerType\":null,\"nestedInstallerTypeCode\":null,"
            + "\"productCode\":null,\"packageFamilyName\":null}],"
            + "\"dependencies\":[{\"payloadPath\":\"fixture.exe\",\"architecture\":\"x64\","
            + "\"architectureCode\":\"x64\",\"kind\":\"dotNetRuntime\","
            + "\"kindCode\":\"dotNetRuntime\",\"status\":\"detected\",\"statusCode\":\"detected\","
            + "\"runtimeMajor\":10,"
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
    public async Task Analyze_redacts_every_remote_input_query_value()
    {
        var analyzer = new FakeInstallerDiagnosticService { IsRemote = true };
        CliHarness harness = CreateHarness(analyzer: analyzer);

        CliRunResult result = await harness.RunAsync(
        [
            "analyze",
            "https://example.test/setup.exe?download_key=opaque-secret&page=2",
            "--format",
            "json",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("opaque-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("page=2", result.StandardOutput, StringComparison.Ordinal);
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
        Assert.Contains(
            "Downloading and validating manifests",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"isValid\":false,\"canProceed\":false,"
            + "\"networkMode\":\"offline\",\"networkModeCode\":\"offline\","
            + "\"warningPolicy\":\"allow\",\"warningPolicyCode\":\"allow\","
            + "\"files\":[\"C:\\\\fixture\\\\Example.App.installer.yaml\"],"
            + "\"findings\":[{\"code\":\"VLD6001\",\"severity\":\"error\","
            + "\"severityCode\":\"error\","
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
            "{\"schemaVersion\":\"1.0\",\"repository\":\"microsoft/winget-pkgs\",\"reference\":\"main\","
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
    public async Task Public_repository_reads_work_anonymously_and_receive_ghes_options()
    {
        var repository = new FakeRepositoryDiagnosticService();
        string? observedToken = "not-called";
        GitHubClientOptions? observedOptions = null;
        var harness = new CliHarness();
        harness.Modules.Add(new DiagnosticsCommandModule(
            new FakeInstallerDiagnosticService(),
            new FakeManifestValidationService(),
            publicRepositoryServiceFactory: (options, token) =>
            {
                observedOptions = options;
                observedToken = token;
                return repository;
            }));

        CliRunResult result = await harness.RunAsync(
        [
            "show",
            "Example.App",
            "2.0.0",
            "--github-api-url",
            "https://ghe.example.test/api/v3/",
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Null(observedToken);
        Assert.Equal(
            "https://ghe.example.test/api/v3/",
            observedOptions!.ApiBaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task Public_repository_read_continues_when_optional_keyring_lookup_fails()
    {
        var repository = new FakeRepositoryDiagnosticService();
        string? observedToken = "not-called";
        var harness = new CliHarness();
        harness.TokenStore.GetFailure = new IOException("secret-tool could not start");
        harness.Modules.Add(new DiagnosticsCommandModule(
            new FakeInstallerDiagnosticService(),
            new FakeManifestValidationService(),
            publicRepositoryServiceFactory: (_, token) =>
            {
                observedToken = token;
                return repository;
            }));

        CliRunResult result = await harness.RunAsync(
            ["list-versions", "Example.App"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Null(observedToken);
        Assert.Contains("continuing with an anonymous public read", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_timeout_is_an_operation_failure_not_user_cancellation()
    {
        var repository = new FakeRepositoryDiagnosticService
        {
            Failure = new TaskCanceledException("HTTP timeout"),
        };
        CliHarness harness = CreateHarness(repository: repository);
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token";

        CliRunResult result = await harness.RunAsync(
            ["show", "Example.App", "2.0.0"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("remote request timed out", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_transport_failure_is_an_operation_failure()
    {
        var repository = new FakeRepositoryDiagnosticService
        {
            Failure = new HttpRequestException("DNS failure"),
        };
        CliHarness harness = CreateHarness(repository: repository);
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token";

        CliRunResult result = await harness.RunAsync(
            ["list-versions", "Example.App"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("DNS failure", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_maps_to_cancelled_without_stack_trace()
    {
        using var cancellation = new CancellationTokenSource();
        var analyzer = new FakeInstallerDiagnosticService
        {
            Cancellation = cancellation,
        };
        CliHarness harness = CreateHarness(analyzer: analyzer);

        CliRunResult result = await harness.RunAsync(
            ["analyze", "fixture.exe"],
            cancellation.Token);

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
    public bool IsRemote { get; init; }

    public Exception? Failure { get; init; }

    public CancellationTokenSource? Cancellation { get; init; }

    public Task<InstallerDiagnosticResult> AnalyzeAsync(
        InstallerAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Cancellation is not null)
        {
            Cancellation.Cancel();
            return Task.FromCanceled<InstallerDiagnosticResult>(cancellationToken);
        }

        if (Failure is not null)
        {
            return Task.FromException<InstallerDiagnosticResult>(Failure);
        }

        return Task.FromResult(new InstallerDiagnosticResult(
            request.Input,
            "fixture.exe",
            IsRemote,
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
    public Exception? Failure { get; init; }

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
        if (Failure is not null)
        {
            return Task.FromException<PackageVersionResult>(Failure);
        }

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
        if (Failure is not null)
        {
            return Task.FromException<PackageVersionsResult>(Failure);
        }

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
