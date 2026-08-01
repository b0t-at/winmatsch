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
        foreach (InstallerArtifact artifact in request.LocalPlan.Preflight.InstallerArtifacts)
        {
            try
            {
                DownloadRevalidationResult result = await _downloader.RevalidateAsync(
                    artifact.Download,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Status == DownloadRevalidationStatus.ContentChanged)
                {
                    diagnostics.Add(new(
                        "GH1019",
                        $"Installer content changed after planning: {artifact.InstallerUrl}"));
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
                    $"Installer revalidation failed for {artifact.InstallerUrl}: "
                    + GitHubSubmissionFormatter.Redact(exception.Message)));
            }
        }

        return diagnostics.Count == 0
            ? FinalArtifactRevalidationResult.Valid
            : new(false, diagnostics.ToImmutable());
    }
}
