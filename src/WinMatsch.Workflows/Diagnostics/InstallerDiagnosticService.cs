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
                    download.Sha256.Value,
                    download.SizeInBytes);
            }
            else if (uri is not null && !uri.IsFile)
            {
                throw new NotSupportedException(
                    $"Installer URI scheme '{uri.Scheme}' is unsupported. Use a local path or HTTPS URL.");
            }
            else
            {
                string path = Path.GetFullPath(request.Input);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"Installer file '{request.Input}' does not exist.", path);
                }

                var fileInfo = new FileInfo(path);
                string sha256 = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                installer = new ResolvedInstaller(
                    path,
                    fileInfo.Name,
                    false,
                    false,
                    sha256,
                    fileInfo.Length);
            }

            if (!FileAnalyzer.CanAnalyze(installer.FileName))
            {
                throw new NotSupportedException(
                    $"No installer analyzer supports '{Path.GetExtension(installer.FileName)}'. Manual analysis is required.");
            }

            InstallerAnalysis analysis;
            PayloadDependencyAnalysis dependencies;
            await using (FileStream stream = File.OpenRead(installer.Path))
            {
                analysis = FileAnalyzer.Analyze(stream, installer.FileName);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using (FileStream stream = File.OpenRead(installer.Path))
            {
                dependencies = new PayloadDependencyAnalyzer().Analyze(stream, installer.FileName);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string confidence = GetConfidence(analysis);
            return new InstallerDiagnosticResult(
                request.Input,
                installer.FileName,
                installer.IsRemote,
                installer.IsFromCache,
                installer.Sha256,
                installer.SizeInBytes,
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

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
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
        string Sha256,
        long SizeInBytes);
}
