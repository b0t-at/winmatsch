using System.Collections.Immutable;
using WinMatsch.Downloads;
using WinMatsch.Validation;

namespace WinMatsch.Workflows.GitHub;

/// <summary>Revalidates every acquired installer immediately before the remote mutation boundary.</summary>
public sealed class DownloaderFinalArtifactRevalidator(InstallerDownloader downloader)
    : IFinalArtifactRevalidator
{
    private readonly InstallerDownloader _downloader =
        downloader ?? throw new ArgumentNullException(nameof(downloader));

    public async Task<FinalArtifactRevalidationResult> RevalidateAsync(
        GitHubSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        string scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-final-revalidation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
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
        }
        finally
        {
            if (Directory.Exists(scratchDirectory))
            {
                Directory.Delete(scratchDirectory, recursive: true);
            }
        }

        return diagnostics.Count == 0
            ? FinalArtifactRevalidationResult.Valid
            : new(false, diagnostics.ToImmutable());
    }
}
