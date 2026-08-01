using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinMatsch.Testing.Fixtures;
using WinMatsch.Validation;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class RegressionDescriptorE2ETests
{
    private static readonly string[] _expectedIds =
    [
        "buf",
        "clouddrive2",
        "curl",
        "electron",
        "exiftool",
        "keeper-commander",
        "mise",
        "notesnook",
        "pandoc",
        "sonarr",
        "super-productivity",
        "surrealdb",
        "uhk-agent",
    ];

    [Fact]
    public async Task Synthetic_substitutes_run_the_complete_production_pipeline_to_exact_yaml_goldens()
    {
        Assert.Equal(_expectedIds, FixtureCatalog.All.Select(static fixture => fixture.Descriptor.Id));
        foreach (RegressionFixture fixture in FixtureCatalog.All)
        {
            IReadOnlyDictionary<string, byte[]> assets = RegressionFixturePipeline.BuildAssets(fixture);
            if (Environment.GetEnvironmentVariable("WINMATSCH_UPDATE_REGRESSION_GOLDENS") == "1")
            {
                WriteSyntheticHashes(fixture, assets);
            }
            using var temporary = new TemporaryDirectory();
            RegressionFixturePipeline.WritePreviousManifests(fixture, temporary.Path);
            LocalWorkflowEngine engine = RegressionFixturePipeline.CreateEngine(
                fixture,
                assets,
                out FixtureHttpMessageHandler handler,
                out FixtureReleaseSource releaseSource);
            WorkflowOperationRequest request = RegressionFixturePipeline.CreateRequest(fixture, temporary.Path);

            WorkflowOperationResult result = request switch
            {
                NewOperationRequest create => await engine.NewAsync(create),
                UpdateOperationRequest update => await engine.UpdateAsync(update),
                _ => throw new InvalidDataException($"Unsupported fixture operation '{request.GetType().Name}'."),
            };

            Assert.True(
                result.Code == WorkflowResultCode.Succeeded,
                $"{fixture.Descriptor.Id}:{Environment.NewLine}{RegressionFixturePipeline.Describe(result)}");
            Assert.False(result.Applied);
            Assert.Equal(fixture.Descriptor.Assets.Count, releaseSource.DiscoveredCount);
            Assert.All(
                fixture.Descriptor.Assets,
                asset => Assert.Contains(
                    handler.Requests,
                    requestRecord => requestRecord.Method == HttpMethod.Get
                        && requestRecord.Uri == asset.Url));
            Assert.NotEmpty(fixture.Descriptor.Regression.RuleIds);
            Assert.Contains(
                result.Plan.Rules.Executions,
                static execution => execution.RuleId == "PIPE-1");
            Assert.Contains(result.Plan.Audit, static entry => entry.Code.StartsWith("MAP_", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Plan.Validation.Findings,
                static finding => finding.Severity == ValidationSeverity.Error);

            if (Environment.GetEnvironmentVariable("WINMATSCH_UPDATE_REGRESSION_GOLDENS") == "1")
            {
                WriteGoldens(fixture, result.Plan.AfterDocuments);
            }
            else
            {
                AssertGoldens(fixture, result.Plan.AfterDocuments);
            }
        }
    }

    [Fact]
    public async Task Upstream_binary_acquisition_remains_checksum_pinned_and_opt_in()
    {
        using var temporary = new TemporaryDirectory();
        var acquirer = new FixtureAcquirer(new HttpClient(), Testing.Infrastructure.PhysicalTestFileSystem.Instance);
        bool allowNetwork = Environment.GetEnvironmentVariable("WINMATSCH_E2E_ACQUIRE_FIXTURES") == "1";

        foreach (FixtureAsset asset in FixtureCatalog.All.SelectMany(static fixture => fixture.Descriptor.Assets))
        {
            FixtureAcquisitionResult result = await acquirer.AcquireAsync(
                asset,
                new FixtureAcquisitionOptions
                {
                    CacheDirectory = temporary.Path,
                    AllowNetwork = allowNetwork,
                });
            if (allowNetwork)
            {
                Assert.True(result.IsAvailable, result.Message);
            }
            else
            {
                Assert.Equal(FixtureAcquisitionStatus.Unavailable, result.Status);
                Assert.Contains("network acquisition is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static void AssertGoldens(
        RegressionFixture fixture,
        IReadOnlyList<RawManifestDocument> actual)
    {
        Dictionary<string, byte[]> byFileName = actual.ToDictionary(
            static document => Path.GetFileName(document.RepositoryPath),
            static document => document.Content.ToArray(),
            StringComparer.Ordinal);
        Assert.Equal(fixture.ExpectedManifests.Keys, byFileName.Keys);
        foreach ((string fileName, byte[] expected) in fixture.ExpectedManifests)
        {
            Assert.True(
                expected.SequenceEqual(byFileName[fileName]),
                $"Fixture '{fixture.Descriptor.Id}' manifest '{fileName}' differs from its complete YAML golden."
                + $"{Environment.NewLine}Expected:{Environment.NewLine}{Encoding.UTF8.GetString(expected)}"
                + $"{Environment.NewLine}Actual:{Environment.NewLine}{Encoding.UTF8.GetString(byFileName[fileName])}");
        }
    }

    private static void WriteGoldens(
        RegressionFixture fixture,
        IReadOnlyList<RawManifestDocument> documents)
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(
            root,
            "tests",
            "WinMatsch.Testing",
            "Fixtures",
            "ExpectedManifests",
            fixture.Descriptor.ExpectedManifestDirectory);
        Directory.CreateDirectory(directory);
        foreach (RawManifestDocument document in documents)
        {
            File.WriteAllBytes(
                Path.Combine(directory, Path.GetFileName(document.RepositoryPath)),
                document.Content.ToArray());
        }
    }

    private static void WriteSyntheticHashes(
        RegressionFixture fixture,
        IReadOnlyDictionary<string, byte[]> assets)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "WinMatsch.Testing",
            "Fixtures",
            "Descriptors",
            $"{fixture.Descriptor.Id}.descriptor.json");
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"Descriptor '{path}' is empty.");
        JsonArray nodes = root["assets"]?.AsArray()
            ?? throw new InvalidDataException($"Descriptor '{path}' has no assets.");
        Assert.Equal(fixture.Descriptor.Assets.Count, nodes.Count);
        for (int index = 0; index < fixture.Descriptor.Assets.Count; index++)
        {
            FixtureAsset asset = fixture.Descriptor.Assets[index];
            nodes[index]!["syntheticSha256"] =
                Convert.ToHexString(SHA256.HashData(assets[asset.Url.AbsoluteUri]));
        }

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinMatsch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root for golden generation.");
    }
}
