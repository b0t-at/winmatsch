using System.Collections.Immutable;
using WinMatsch.Downloads;
using WinMatsch.Validation;

namespace WinMatsch.Workflows.GitHub;

/// <summary>Revalidates every acquired installer immediately before the remote mutation boundary.</summary>
public sealed class DownloaderFinalArtifactRevalidator : IFinalArtifactRevalidator
{
    private readonly InstallerDownloader _downloader;
    private readonly IRevalidationScratchSpace _scratchSpace;

    public DownloaderFinalArtifactRevalidator(InstallerDownloader downloader)
        : this(downloader, new FileRevalidationScratchSpace())
    {
    }

    public DownloaderFinalArtifactRevalidator(
        InstallerDownloader downloader,
        IRevalidationScratchSpace scratchSpace)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _scratchSpace = scratchSpace ?? throw new ArgumentNullException(nameof(scratchSpace));
    }

    public async Task<FinalArtifactRevalidationResult> RevalidateAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        string scratchDirectory = _scratchSpace.Create();
        Exception? cleanupFailure = null;
        bool safetyValidated = false;
        try
        {
            foreach (InstallerArtifact artifact in request.LocalPlan.Preflight.InstallerArtifacts)
            {
                string safeUrl = GitHubSubmissionFormatter.Redact(artifact.InstallerUrl);
                try
                {
                    DownloadResult current = await _downloader.DownloadFreshAsync(
                        artifact.Download.InitialUrl,
                        scratchDirectory,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (current.ContentIdentity != artifact.Download.ContentIdentity)
                    {
                        diagnostics.Add(new(
                            "GH1019",
                            $"Installer content changed after planning: {safeUrl}"));
                    }
                }
                catch (Exception exception) when (
                    exception is DownloadException
                        or HttpRequestException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(
                        "GH1020",
                        $"Installer revalidation failed for {safeUrl}: "
                        + GitHubSubmissionFormatter.Redact(exception.Message)));
                }
            }

            safetyValidated = diagnostics.Count == 0;
        }
        finally
        {
            try
            {
                _scratchSpace.Delete(scratchDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure = exception;
            }
        }

        if (cleanupFailure is not null)
        {
            diagnostics.Add(new(
                "GH1021",
                "Final artifact revalidation completed, but its temporary files could not be removed: "
                + GitHubSubmissionFormatter.Redact(cleanupFailure.Message)));
        }

        return diagnostics.Count == 0
            ? FinalArtifactRevalidationResult.Valid
            : new(safetyValidated, diagnostics.ToImmutable());
    }
}

public sealed class FileRevalidationScratchSpace : IRevalidationScratchSpace
{
    public string Create()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-final-revalidation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
