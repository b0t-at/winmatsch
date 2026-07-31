using System.Security.Cryptography;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Downloads;

namespace WinMatsch.Workflows.Diagnostics;

public interface IInstallerDiagnosticService
{
    public Task<InstallerDiagnosticResult> AnalyzeAsync(
        InstallerAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class InstallerDiagnosticService : IInstallerDiagnosticService
{
    private readonly Func<DownloaderOptions, InstallerDownloader> _downloaderFactory;

    public InstallerDiagnosticService(
        Func<DownloaderOptions, InstallerDownloader>? downloaderFactory = null)
    {
        _downloaderFactory = downloaderFactory
            ?? (static options => new InstallerDownloader(options));
    }

    public async Task<InstallerDiagnosticResult> AnalyzeAsync(
        InstallerAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        cancellationToken.ThrowIfCancellationRequested();

        string? scratchDirectory = null;
        try
        {
            ResolvedInstaller installer;
            bool explicitUri = request.Input.Contains("://", StringComparison.Ordinal);
            if (Uri.TryCreate(request.Input, UriKind.Absolute, out Uri? uri)
                && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        "Installer URLs must use HTTPS. Download the file manually before analyzing another URL scheme.");
                }

                scratchDirectory = CreateScratchDirectory();
                using InstallerDownloader downloader = _downloaderFactory(
                    CreateDownloaderOptions(request.CacheEnabled, request.CacheDirectory));
                DownloadResult download = await downloader
                    .DownloadAsync(uri.AbsoluteUri, scratchDirectory, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                installer = new ResolvedInstaller(
                    download.FilePath,
                    download.FileName,
                    true,
                    download.IsFromCache,
                    download.Sha256.Value);
            }
            else if (explicitUri)
            {
                throw new NotSupportedException(
                    $"Installer URI scheme '{uri?.Scheme ?? "<unknown>"}' is unsupported. Use a local path or HTTPS URL.");
            }
            else
            {
                string path = Path.GetFullPath(request.Input);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"Installer file '{request.Input}' does not exist.", path);
                }

                scratchDirectory = CreateScratchDirectory();
                var fileInfo = new FileInfo(path);
                string snapshotPath = Path.Combine(scratchDirectory, fileInfo.Name);
                await using (FileStream source = File.Open(
                                 path,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read))
                await using (FileStream destination = File.Create(snapshotPath))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                installer = new ResolvedInstaller(
                    snapshotPath,
                    fileInfo.Name,
                    false,
                    false,
                    null);
            }

            if (!FileAnalyzer.CanAnalyze(installer.FileName))
            {
                throw new NotSupportedException(
                    $"No installer analyzer supports '{Path.GetExtension(installer.FileName)}'. Manual analysis is required.");
            }

            InstallerAnalysis analysis;
            PayloadDependencyAnalysis dependencies;
            string sha256;
            long sizeInBytes;
            await using FileStream stream = File.OpenRead(installer.Path);
            sizeInBytes = stream.Length;
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            sha256 = Convert.ToHexString(hash);
            if (installer.ExpectedSha256 is not null
                && !string.Equals(sha256, installer.ExpectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Downloaded installer '{installer.FileName}' changed before analysis.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            analysis = FileAnalyzer.Analyze(stream, installer.FileName);

            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            dependencies = new PayloadDependencyAnalyzer().Analyze(stream, installer.FileName);

            cancellationToken.ThrowIfCancellationRequested();
            string confidence = GetConfidence(analysis);
            return new InstallerDiagnosticResult(
                request.Input,
                installer.FileName,
                installer.IsRemote,
                installer.IsFromCache,
                sha256,
                sizeInBytes,
                confidence,
                analysis,
                dependencies);
        }
        finally
        {
            if (scratchDirectory is not null)
            {
                TryDeleteScratchDirectory(scratchDirectory);
            }
        }
    }

    private static DownloaderOptions CreateDownloaderOptions(bool cacheEnabled, string? cacheDirectory)
        => new()
        {
            CacheDirectory = cacheEnabled
                ? cacheDirectory ?? GetDefaultCacheDirectory()
                : null,
        };

    private static string GetConfidence(InstallerAnalysis analysis)
    {
        if (analysis.Diagnostics.Any(static diagnostic => diagnostic.RequiresManualAnalysis))
        {
            return "manual-analysis-required";
        }

        return analysis.Format == DetectedInstallerFormat.GenericInstallerExe ? "medium" : "high";
    }

    private static string CreateScratchDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "winmatsch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetDefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "winmatsch",
            "downloads");

    private static void TryDeleteScratchDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ResolvedInstaller(
        string Path,
        string FileName,
        bool IsRemote,
        bool IsFromCache,
        string? ExpectedSha256);
}
