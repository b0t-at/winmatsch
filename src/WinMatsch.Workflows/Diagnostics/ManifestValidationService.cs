using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.Validation;

namespace WinMatsch.Workflows.Diagnostics;

public interface IManifestValidationService
{
    public Task<ManifestValidationResult> ValidateAsync(
        ManifestValidationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ManifestValidationService : IManifestValidationService
{
    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Func<DownloaderOptions, InstallerDownloader> _downloaderFactory;

    public ManifestValidationService(
        Func<DownloaderOptions, InstallerDownloader>? downloaderFactory = null)
    {
        _downloaderFactory = downloaderFactory
            ?? (static options => new InstallerDownloader(options));
    }

    public async Task<ManifestValidationResult> ValidateAsync(
        ManifestValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        if (request.Paths.Count == 0)
        {
            throw new ArgumentException("At least one manifest path is required.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> paths = ResolveFiles(request.Paths);
        ManifestDocument[] documents = await ReadDocumentsAsync(paths, cancellationToken).ConfigureAwait(false);
        var changes = documents
            .Select(static document => new RepositoryFileChange(
                document.RepositoryPath,
                RepositoryChangeKind.Added))
            .ToArray();

        NetworkValidationMode mode = request.Offline
            ? NetworkValidationMode.Offline
            : NetworkValidationMode.Online;
        var options = new PreflightOptions
        {
            NetworkMode = mode,
            WarningPolicy = request.WarningPolicy,
        };

        string? scratchDirectory = null;
        var additionalFindings = new List<ValidationFinding>();
        try
        {
            if (request.Offline)
            {
                ValidationReport offlineReport = await new PreflightGate()
                    .ValidateAsync(
                        new PreflightRequest
                        {
                            Documents = documents,
                            Changes = changes,
                            Options = options,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return CreateResult(mode, request.WarningPolicy, paths, offlineReport);
            }

            scratchDirectory = CreateScratchDirectory();
            using InstallerDownloader downloader = _downloaderFactory(new DownloaderOptions
            {
                CacheDirectory = request.CacheEnabled
                    ? request.CacheDirectory ?? GetDefaultCacheDirectory()
                    : null,
            });

            IReadOnlyList<string> urls = FindInstallerUrls(documents);
            IReadOnlyList<InstallerArtifact> artifacts = await DownloadArtifactsAsync(
                    urls,
                    scratchDirectory,
                    downloader,
                    Math.Max(1, request.ConcurrentDownloads),
                    additionalFindings,
                    cancellationToken)
                .ConfigureAwait(false);

            ValidationReport report = await new PreflightGate(
                    new InstallerDownloaderPreflightNetwork(
                        downloader,
                        Path.Combine(scratchDirectory, "revalidation")))
                .ValidateAsync(
                    new PreflightRequest
                    {
                        Documents = documents,
                        Changes = changes,
                        InstallerArtifacts = artifacts,
                        Options = options,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            ValidationReport combined = CreateStableReport(
                report.Findings.Concat(additionalFindings));
            return CreateResult(mode, request.WarningPolicy, paths, combined);
        }
        finally
        {
            if (scratchDirectory is not null)
            {
                TryDeleteScratchDirectory(scratchDirectory);
            }
        }
    }

    private static IReadOnlyList<string> ResolveFiles(IReadOnlyList<string> inputs)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string input in inputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input);
            string fullPath = Path.GetFullPath(input);
            if (Directory.Exists(fullPath))
            {
                foreach (string path in Directory.EnumerateFiles(fullPath)
                             .Where(IsYamlFile)
                             .OrderBy(static path => path, StringComparer.Ordinal))
                {
                    files.Add(path);
                }
            }
            else if (File.Exists(fullPath))
            {
                if (!IsYamlFile(fullPath))
                {
                    throw new InvalidDataException($"Manifest file '{input}' must use .yaml or .yml.");
                }

                files.Add(fullPath);
            }
            else
            {
                throw new FileNotFoundException($"Manifest path '{input}' does not exist.", fullPath);
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException("No YAML manifest files were found.");
        }

        return [.. files];
    }

    private static async Task<ManifestDocument[]> ReadDocumentsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var documents = new List<ManifestDocument>(paths.Count);
        foreach (string path in paths)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string content = _strictUtf8.GetString(bytes);
            string repositoryPath = GetRepositoryPath(path, content);
            documents.Add(new ManifestDocument(repositoryPath, content));
        }

        return [.. documents];
    }

    private static IReadOnlyList<string> FindInstallerUrls(
        IReadOnlyList<ManifestDocument> documents)
    {
        var urls = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ManifestDocument document in documents)
        {
            try
            {
                if (ManifestYamlReader.TryDetectType(document.Content) != ManifestType.Installer)
                {
                    continue;
                }

                InstallerManifest manifest = ManifestYamlReader.ReadInstaller(document.Content);
                foreach (Installer installer in manifest.Installers ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(installer.InstallerUrl))
                    {
                        urls.Add(installer.InstallerUrl);
                    }
                }
            }
            catch (Exception exception)
                when (exception is FormatException or ArgumentException or YamlDotNet.Core.YamlException)
            {
                // The preflight parser reports malformed manifest input with path-aware findings.
            }
        }

        return [.. urls];
    }

    private static async Task<IReadOnlyList<InstallerArtifact>> DownloadArtifactsAsync(
        IReadOnlyList<string> urls,
        string destinationDirectory,
        InstallerDownloader downloader,
        int concurrency,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = urls.Select(async url =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DownloadResult result = await downloader
                    .DownloadAsync(url, destinationDirectory, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return new DownloadOutcome(url, result, null);
            }
            catch (Exception exception)
                when (exception is DownloadException or ArgumentException or InvalidOperationException)
            {
                return new DownloadOutcome(url, null, exception.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        DownloadOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        var artifacts = new List<InstallerArtifact>();
        foreach (DownloadOutcome outcome in outcomes.OrderBy(static item => item.Url, StringComparer.Ordinal))
        {
            if (outcome.Result is not null)
            {
                artifacts.Add(new InstallerArtifact(outcome.Url, outcome.Result));
            }
            else
            {
                findings.Add(new ValidationFinding(
                    "VLD6012",
                    ValidationSeverity.Error,
                    $"Installer download failed before SHA validation: {outcome.Error}",
                    outcome.Url));
            }
        }

        return artifacts;
    }

    private static ManifestValidationResult CreateResult(
        NetworkValidationMode mode,
        WarningPolicy warningPolicy,
        IReadOnlyList<string> paths,
        ValidationReport report)
        => new(mode, warningPolicy, paths, report);

    private static ValidationReport CreateStableReport(IEnumerable<ValidationFinding> findings)
        => new(
            findings
                .Distinct()
                .OrderBy(static finding => finding.Code, StringComparer.Ordinal)
                .ThenBy(static finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(static finding => finding.Message, StringComparer.Ordinal));

    private static string GetRepositoryPath(string path, string content)
    {
        try
        {
            ManifestHeader header = ManifestYamlReader.ReadHeader(content);
            if (PackageIdentifier.TryCreate(header.PackageIdentifier, out PackageIdentifier? identifier)
                && PackageVersion.TryCreate(header.PackageVersion, out PackageVersion? version))
            {
                return $"{ManifestPaths.GetVersionDirectory(identifier!, version!)}/{Path.GetFileName(path)}";
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
        }

        return Path.GetFileName(path);
    }

    private static bool IsYamlFile(string path)
        => Path.GetExtension(path) is { } extension
            && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));

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

    private sealed record DownloadOutcome(string Url, DownloadResult? Result, string? Error);
}
