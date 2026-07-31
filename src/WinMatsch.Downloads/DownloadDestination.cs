using WinMatsch.Core;

namespace WinMatsch.Downloads;

internal static class DownloadDestination
{
    private const int CopyBufferSize = 81920;

    public static async Task<string> PublishAsync(
        string temporaryPath,
        string preferredPath,
        DownloadContentIdentity identity,
        CancellationToken cancellationToken)
    {
        string normalizedPreferredPath = Path.GetFullPath(preferredPath);
        PublishOutcome preferredOutcome = await TryPublishAsync(
            temporaryPath,
            normalizedPreferredPath,
            identity,
            cancellationToken).ConfigureAwait(false);
        if (preferredOutcome != PublishOutcome.Conflict)
        {
            return normalizedPreferredPath;
        }

        string contentPath = GetContentAddressedPath(normalizedPreferredPath, identity.Sha256);
        PublishOutcome contentOutcome = await TryPublishAsync(
            temporaryPath,
            contentPath,
            identity,
            cancellationToken).ConfigureAwait(false);
        if (contentOutcome == PublishOutcome.Conflict)
        {
            throw new DownloadFileException(
                contentPath,
                $"The content-addressed destination '{contentPath}' contains bytes that do not match its SHA-256 name.",
                new InvalidDataException("A content-addressed destination collision was detected."));
        }

        return contentPath;
    }

    private static async Task<PublishOutcome> TryPublishAsync(
        string temporaryPath,
        string destinationPath,
        DownloadContentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            DownloadContentIdentity existing = await ComputeIdentityAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (existing == identity)
            {
                TryDelete(temporaryPath);
                return PublishOutcome.Matched;
            }

            return PublishOutcome.Conflict;
        }

        try
        {
            File.Move(temporaryPath, destinationPath);
            return PublishOutcome.Published;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            DownloadContentIdentity existing = await ComputeIdentityAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (existing == identity)
            {
                TryDelete(temporaryPath);
                return PublishOutcome.Matched;
            }

            return PublishOutcome.Conflict;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException(
                destinationPath,
                $"Failed to atomically publish the installer to '{destinationPath}'.",
                exception);
        }
    }

    private static string GetContentAddressedPath(string preferredPath, Sha256Hash sha256)
    {
        string? directory = Path.GetDirectoryName(preferredPath);
        if (directory is null)
        {
            throw new InvalidOperationException($"The destination '{preferredPath}' has no parent directory.");
        }

        string extension = Path.GetExtension(preferredPath);
        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        return Path.Combine(directory, $"sha256-{sha256.Normalized.ToLowerInvariant()}{extension}");
    }

    private static async Task<DownloadContentIdentity> ComputeIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Sha256Hash sha256 = await Sha256Hash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
            return new DownloadContentIdentity(sha256, stream.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadFileException(
                path,
                $"Failed to verify the existing destination '{path}'.",
                exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private enum PublishOutcome
    {
        Published,
        Matched,
        Conflict,
    }
}
