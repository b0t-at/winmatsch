using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows.Configuration;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Mutations;

public sealed class ProductionMutationWorkflowFactory : IMutationWorkflowFactory
{
    public Task<IMutationWorkflow> CreateAsync(
        Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMutationWorkflow>(
            new ProductionMutationWorkflow(context.Configuration, context.Tokens));
    }
}

public sealed class ProductionSubmissionWorkflowFactory : ISubmissionWorkflowFactory
{
    public async Task<ISubmissionWorkflow> CreateAsync(
        Hosting.CommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ResolvedToken token = await context.Tokens.RequireAsync(cancellationToken).ConfigureAwait(false);
        return new ProductionSubmissionWorkflow(context.Configuration, token.Token);
    }
}

internal sealed class ProductionMutationWorkflow(
    WinMatschConfiguration configuration,
    Hosting.ITokenAccessor tokens) : IMutationWorkflow
{
    public async Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var downloader = new InstallerDownloader(DownloaderOptions(configuration));
        using HttpClient? gitHubHttp = await CreateReleaseHttpClientAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using IGitHubRepositoryClient? gitHub = gitHubHttp is null
            ? null
            : new GitHubRepositoryClient(
                gitHubHttp,
                (await tokens.RequireAsync(cancellationToken).ConfigureAwait(false)).Token.RevealValue());
        IWorkflowReleaseSource? releases = CreateReleaseSource(request, gitHub);
        LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
            downloader,
            releases,
            overridePackStoreOptions: configuration.OverrideStoreDirectory is null
                ? null
                : new OverridePackStoreOptions
                {
                    RootDirectory = configuration.OverrideStoreDirectory,
                });
        return await new LocalMutationWorkflow(engine)
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IWorkflowReleaseSource? CreateReleaseSource(
        WorkflowOperationRequest request,
        IGitHubRepositoryClient? gitHub)
    {
        ReleaseRequest? release = request switch
        {
            NewOperationRequest value => value.Release,
            UpdateOperationRequest value => value.Release,
            _ => null,
        };
        if (release is null)
        {
            return null;
        }

        if (!release.ReleaseUrls.IsEmpty)
        {
            RepositoryCoordinates repository = ParseGitHubRepository(release.ReleaseUrls[0]);
            IGitHubRepositoryClient requiredGitHub = gitHub
                ?? throw new InvalidOperationException(
                    "GitHub release discovery requires an invocation-scoped GitHub client.");
            return new GitHubWorkflowReleaseSource(
                requiredGitHub,
                repository,
                new GitHubRepositoryReleaseMetadataSource(requiredGitHub));
        }

        return release.InstallerUrls.IsEmpty ? null : new DirectWorkflowReleaseSource();
    }

    private static async Task<HttpClient?> CreateReleaseHttpClientAsync(
        WorkflowOperationRequest request,
        CancellationToken cancellationToken)
    {
        ReleaseRequest? release = request switch
        {
            NewOperationRequest value => value.Release,
            UpdateOperationRequest value => value.Release,
            _ => null,
        };
        if (release is null || release.ReleaseUrls.IsEmpty)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = ParseGitHubRepository(release.ReleaseUrls[0]);
        await Task.CompletedTask.ConfigureAwait(false);
        return new HttpClient();
    }

    private static RepositoryCoordinates ParseGitHubRepository(Uri releaseUri)
    {
        if (!releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Release URL '{releaseUri}' must be an HTTPS github.com repository URL.");
        }

        string[] segments = releaseUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            throw new FormatException(
                $"Release URL '{releaseUri}' does not identify a GitHub owner and repository.");
        }

        return new(segments[0], segments[1]);
    }

    private static DownloaderOptions DownloaderOptions(WinMatschConfiguration configuration)
        => new()
        {
            CacheDirectory = configuration.CacheEnabled
                ? configuration.CacheDirectory ?? DefaultCacheDirectory()
                : null,
        };

    private static string DefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "winmatsch",
            "downloads");
}

internal sealed class ProductionSubmissionWorkflow(
    WinMatschConfiguration configuration,
    GitHubToken token) : ISubmissionWorkflow
{
    public async Task<GitHubLifecycleResult> ExecuteAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        using var gitHub = new GitHubRepositoryClient(httpClient, token.RevealValue());
        using var downloader = new InstallerDownloader(new DownloaderOptions
        {
            CacheDirectory = configuration.CacheEnabled
                ? configuration.CacheDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "winmatsch",
                    "downloads")
                : null,
        });
        GitHubLifecycleWorkflow workflow =
            WorkflowProductionComposition.CreateGitHubLifecycle(gitHub, downloader);
        return await new LifecycleSubmissionWorkflow(workflow)
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
