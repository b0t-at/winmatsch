using System.Text.Json;
using WinMatsch.Cli.Commands.Mutations;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class ResultJsonTests
{
    [Fact]
    public async Task Successful_update_writes_identity_and_absolute_manifest_path()
    {
        using var temporary = new TemporaryDirectory();
        string output = Path.Combine(temporary.Path, "output");
        string resultPath = Path.Combine(temporary.Path, "results", "update.json");
        var harness = CreateHarness(new FakeMutationWorkflow());
        const string token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";

        CliRunResult result = await harness.RunAsync(
        [
            "update",
            "Example.App",
            "1.0",
            "--output",
            output,
            "--token",
            token,
            "--result-json",
            resultPath,
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Applied: true", result.StandardOutput, StringComparison.Ordinal);
        string json = await File.ReadAllTextAsync(resultPath);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("update", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.Success, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("Example.App", root.GetProperty("packageIdentifier").GetString());
        Assert.Equal("1.0", root.GetProperty("packageVersion").GetString());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(output, "manifests", "e", "Example", "App", "1.0")),
            root.GetProperty("manifestPath").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pullRequest").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Successful_submit_writes_created_pull_request()
    {
        using var temporary = new TemporaryDirectory();
        string resultPath = Path.Combine(temporary.Path, "submit.json");
        var submissions = new FakeSubmissionWorkflow();
        CliHarness harness = CreateHarness(new FakeMutationWorkflow(), submissions);

        CliRunResult result = await harness.RunAsync(
        [
            "update",
            "Example.App",
            "1.0",
            "--submit",
            "--yes",
            "--result-json",
            resultPath,
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(resultPath));
        JsonElement pullRequest = document.RootElement.GetProperty("pullRequest");
        Assert.Equal("https://example.test/pull/42", pullRequest.GetProperty("url").GetString());
        Assert.Equal(42, pullRequest.GetProperty("number").GetInt64());
    }

    [Fact]
    public async Task Questions_required_writes_stable_domain_code_and_exit_four()
    {
        using var temporary = new TemporaryDirectory();
        string resultPath = Path.Combine(temporary.Path, "failure.json");
        var workflow = new FakeMutationWorkflow
        {
            Handler = request => FakeMutationWorkflow.Result(
                request,
                WorkflowResultCode.QuestionsRequired,
                questions:
                [
                    new(
                        "MAP_REMOVED",
                        "Previous installer assets have no compatible release assets.",
                        []),
                ]),
        };
        CliHarness harness = CreateHarness(workflow);

        CliRunResult result = await harness.RunAsync(
        [
            "update",
            "Example.App",
            "1.0",
            "--interaction",
            "never",
            "--result-json",
            resultPath,
        ]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(resultPath));
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.MissingInput, root.GetProperty("exitCode").GetInt32());
        JsonElement error = root.GetProperty("error");
        Assert.Equal("MAP_REMOVED", error.GetProperty("code").GetString());
        Assert.Contains(
            "Previous installer assets",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_error_writes_result_without_echoing_token()
    {
        using var temporary = new TemporaryDirectory();
        string resultPath = Path.Combine(temporary.Path, "usage.json");
        const string token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(
        [
            "probe",
            "--token",
            token,
            "--unknown",
            "--result-json",
            resultPath,
        ]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        string json = await File.ReadAllTextAsync(resultPath);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("probe", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.UsageError, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("USAGE_ERROR", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("packageIdentifier").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("manifestPath").ValueKind);
    }

    [Fact]
    public async Task Result_file_failure_warns_without_changing_exit_code()
    {
        using var temporary = new TemporaryDirectory();
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule());

        CliRunResult result = await harness.RunAsync(
            ["probe", "--result-json", temporary.Path]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "Warning: failed to write result JSON:",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private static CliHarness CreateHarness(
        FakeMutationWorkflow workflow,
        ISubmissionWorkflow? submissions = null)
    {
        var harness = new CliHarness();
        harness.Modules.Add(new MutationCommandModule(
            workflow,
            submissions,
            new FakeEditorRunner(),
            new FakeManifestLoader(),
            new FakeUrlLauncher()));
        return harness;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("winmatsch-result-json-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
