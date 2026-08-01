using System.Net;
using System.Text;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

/// <summary>
/// Unit coverage for the maintenance adapters: dead-artifact classification, upstream
/// dead-version inspection, and installer URL extraction from remote manifests.
/// </summary>
public sealed class MaintenanceAdapterTests
{
    private static readonly RepositoryCoordinates _upstream = new("microsoft", "winget-pkgs");
    private static readonly PackageIdentifier _package = new("Contoso.App");
    private static readonly PackageVersion _version = new("1.0.0");

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DeadArtifactState.PermanentlyMissing)]
    [InlineData(HttpStatusCode.Gone, DeadArtifactState.PermanentlyMissing)]
    [InlineData(HttpStatusCode.Unauthorized, DeadArtifactState.NetworkBlocked)]
    [InlineData(HttpStatusCode.Forbidden, DeadArtifactState.NetworkBlocked)]
    [InlineData(HttpStatusCode.BadRequest, DeadArtifactState.NetworkBlocked)]
    public void Only_confirmed_absence_statuses_count_as_dead(
        HttpStatusCode status,
        DeadArtifactState expected)
    {
        var exception = new DownloadHttpException(status, "https://example.invalid/app.exe");

        Assert.Equal(expected, HttpInstallerUrlProber.Classify(exception));
    }

    [Fact]
    public void Redirect_and_transient_failures_never_count_as_dead()
    {
        var redirect = new DownloadRedirectException("https://example.invalid/app.exe", 10);
        var transient = new DownloadNetworkException("timeout", new HttpRequestException("timeout"));

        Assert.Equal(DeadArtifactState.NetworkBlocked, HttpInstallerUrlProber.Classify(redirect));
        Assert.Equal(DeadArtifactState.TransientFailure, HttpInstallerUrlProber.Classify(transient));
    }

    [Fact]
    public async Task Head_absence_is_confirmed_with_a_ranged_get_before_counting_as_dead()
    {
        var origin = new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent([0]),
                });
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0]),
            }));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.Exists, state);
    }

    [Fact]
    public async Task Absence_confirmed_by_both_head_and_get_counts_as_dead()
    {
        var origin = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone)));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.PermanentlyMissing, state);
    }

    [Fact]
    public async Task Indeterminate_absence_confirmation_escalates()
    {
        var origin = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var prober = new HttpInstallerUrlProber(
            new InstallerDownloader(origin),
            () => new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        DeadArtifactState state = await prober.ProbeAsync(
            "https://example.invalid/app.exe",
            CancellationToken.None);

        Assert.Equal(DeadArtifactState.NetworkBlocked, state);
    }

    [Fact]
    public async Task Missing_version_directories_report_not_existing_upstream()
    {
        var client = new FakeMaintenanceGitHubClient();
        client.DefaultBranches[_upstream.ToString()] = new BranchState("master", "sha", IsProtected: true);
        var inspector = new GitHubDeadVersionInspector(client, new ThrowingProber());

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.False(inspection.ExistsUpstream);
        Assert.Empty(inspection.ArtifactStates);
    }

    [Fact]
    public async Task Malformed_installer_manifests_classify_as_indeterminate()
    {
        FakeMaintenanceGitHubClient client = WithInstallerManifest("InstallerUrl: [unclosed\n  - broken");
        var inspector = new GitHubDeadVersionInspector(client, new ThrowingProber());

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.True(inspection.ExistsUpstream);
        Assert.Equal([DeadArtifactState.TransientFailure], inspection.ArtifactStates.ToArray());
    }

    [Fact]
    public async Task Installer_urls_are_probed_in_manifest_order()
    {
        FakeMaintenanceGitHubClient client = WithInstallerManifest(
            "PackageIdentifier: Contoso.App\n"
            + "Installers:\n"
            + "  - Architecture: x64\n"
            + "    InstallerUrl: https://example.invalid/a.exe\n"
            + "  - Architecture: x86\n"
            + "    InstallerUrl: https://example.invalid/b.exe\n");
        var prober = new ScriptedProber
        {
            States =
            {
                ["https://example.invalid/a.exe"] = DeadArtifactState.PermanentlyMissing,
                ["https://example.invalid/b.exe"] = DeadArtifactState.Exists,
            },
        };
        var inspector = new GitHubDeadVersionInspector(client, prober);

        DeadVersionInspection inspection = await inspector.InspectAsync(
            _upstream, _package, _version, CancellationToken.None);

        Assert.True(inspection.ExistsUpstream);
        Assert.Equal(
            [DeadArtifactState.PermanentlyMissing, DeadArtifactState.Exists],
            inspection.ArtifactStates.ToArray());
    }

    [Fact]
    public void Extracts_every_installer_url()
    {
        byte[] manifest = Encoding.UTF8.GetBytes(
            "Installers:\n"
            + "  - InstallerUrl: https://example.invalid/a.exe\n"
            + "  - InstallerUrl: https://example.invalid/b.exe\n");

        IReadOnlyList<string> urls = GitHubDeadVersionInspector.ExtractInstallerUrls(manifest);

        Assert.Equal(["https://example.invalid/a.exe", "https://example.invalid/b.exe"], urls);
    }

    private static FakeMaintenanceGitHubClient WithInstallerManifest(string yaml)
    {
        var client = new FakeMaintenanceGitHubClient();
        client.DefaultBranches[_upstream.ToString()] = new BranchState("master", "sha", IsProtected: true);
        string directory = ManifestPaths.GetVersionDirectory(_package, _version);
        client.ManifestFiles[directory] =
        [
            new ManifestFile(
                $"{directory}/{ManifestPaths.GetInstallerFileName(_package)}",
                "file-sha",
                Encoding.UTF8.GetBytes(yaml)),
        ];
        return client;
    }

    private sealed class ThrowingProber : IInstallerUrlProber
    {
        public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The prober must not run in this scenario.");
    }

    private sealed class ScriptedProber : IInstallerUrlProber
    {
        public Dictionary<string, DeadArtifactState> States { get; } = new(StringComparer.Ordinal);

        public Task<DeadArtifactState> ProbeAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult(States[url]);
    }
}
