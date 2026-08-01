using System.Net;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using WinMatsch.Workflows.Tests.GitHub;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class WorkflowProductionCompositionTests
{
    [Fact]
    public async Task Production_local_engine_runs_a_full_new_plan_with_direct_urls()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
            Headers = { ETag = new("\"fixture\"") },
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        try
        {
            WorkflowOperationResult result = await engine.NewAsync(new NewOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PackageVersion = "1.0.0",
                Release = new(
                    null,
                    [new Uri("https://example.test/setup.exe")],
                    []),
                Locale = Locale(),
            });

            Assert.True(
                result.Code == WorkflowResultCode.Succeeded,
                string.Join(
                    Environment.NewLine,
                    [$"Code: {result.Code}", .. result.Plan.Validation.Findings.Select(static finding =>
                        $"{finding.Code}: {finding.Message}"), .. result.Plan.Questions.Select(static question =>
                        $"{question.Code}: {question.Prompt}")]));
            Assert.False(result.Applied);
            Assert.NotEmpty(result.Plan.FileChanges);
            Assert.NotEmpty(result.Plan.Rules.Executions);
            Assert.Contains(
                result.Plan.Rules.Executions,
                static execution => execution.RuleId == Rules.RuleCatalogueIds.Pipe1);
            Assert.True(handler.Requests.Count >= 3);
            Assert.Empty(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Production_local_engine_passes_previous_manifest_on_update()
    {
        byte[] executable = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "WinMatsch.Workflows.Tests.dll"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(executable),
            Headers = { ETag = new("\"fixture\"") },
        });
        using var downloader = new InstallerDownloader(handler);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            new DirectWorkflowReleaseSource());
        string output = CreateDirectory();
        try
        {
            WritePrevious(output);
            WorkflowOperationResult result = await engine.UpdateAsync(new UpdateOperationRequest
            {
                OutputDirectory = output,
                PackageIdentifier = new PackageIdentifier("Example.Composed"),
                PreviousVersion = new PackageVersion("1.0.0"),
                PackageVersion = "2.0.0",
                AllowStructuralRewrite = true,
                AllowStableUrlContentChange = true,
                Release = new(
                    null,
                    [new Uri("https://example.test/setup.exe")],
                    []),
            });

            Assert.True(
                result.Code == WorkflowResultCode.Succeeded,
                string.Join(
                    Environment.NewLine,
                    [$"Code: {result.Code}", .. result.Plan.Validation.Findings.Select(static finding =>
                            $"{finding.Code}: {finding.Message}"), .. result.Plan.Questions.Select(static question =>
                            $"{question.Code}: {question.Prompt}")]));
            Assert.Contains(
                result.Plan.Rules.Executions,
                static execution => execution.RuleId == Rules.RuleIds.PreserveOnUpdate);
            Assert.NotEmpty(result.Plan.Rules.Changes);
            Assert.False(result.Applied);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Composed_github_plan_performs_no_network_or_remote_mutation()
    {
        var client = new FakeGitHubClient();
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Plan-only GitHub composition must not use artifact network."));
        using var downloader = new InstallerDownloader(handler);
        GitHubLifecycleWorkflow workflow = WorkflowProductionComposition.CreateGitHubLifecycle(
            client,
            downloader);

        GitHubLifecycleResult result = await workflow.ExecuteAsync(
            GitHubLifecycleTestSupport.Request(WorkflowExecutionMode.Plan));

        Assert.Equal(GitHubLifecycleResultCode.Planned, result.Code);
        Assert.Empty(client.Mutations);
        Assert.Empty(handler.Requests);
    }

    private static PackageLocaleMetadata Locale() => new()
    {
        PackageLocale = new LanguageTag("en-US"),
        Publisher = "Example",
        PackageName = "Composed",
        License = "MIT",
        ShortDescription = "Composition test",
    };

    private static void WritePrevious(string output)
    {
        var identifier = new PackageIdentifier("Example.Composed");
        var version = new PackageVersion("1.0.0");
        var manifests = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = new LanguageTag("en-US"),
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Portable,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://example.test/setup.exe",
                        InstallerSha256 = new Sha256Hash(
                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "Example",
                PackageName = "Composed",
                License = "MIT",
                ShortDescription = "Composition test",
            },
            Locales = [],
        };
        string directory = Path.Combine(
            output,
            ManifestPaths.GetVersionDirectory(identifier, version).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach ((string fileName, string content) in PackageManifestIO.SerializeFiles(manifests))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "winmatsch-composition-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
