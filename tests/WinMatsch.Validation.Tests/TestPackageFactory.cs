using System.Security.Cryptography;
using WinMatsch.Core;
using WinMatsch.Downloads;

namespace WinMatsch.Validation.Tests;

internal static class TestPackageFactory
{
    public const string Identifier = "Example.App";
    public const string Version = "2.0.0";
    public const string InstallerUrl = "https://example.com/setup.exe";
    public const string PublisherUrl = "https://example.com";
    public const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static PackageManifests CreateManifests()
    {
        var identifier = new PackageIdentifier(Identifier);
        var version = new PackageVersion(Version);
        var locale = new LanguageTag("en-US");
        return new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Exe,
                Scope = Scope.Machine,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = InstallerUrl,
                        InstallerSha256 = new Sha256Hash(Hash),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PublisherUrl = PublisherUrl,
                PackageName = "Example App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            Locales = [],
        };
    }

    public static PreflightRequest CreateRequest(
        PackageManifests? manifests = null,
        PreflightOptions? options = null,
        IReadOnlyList<ExistingVersionSnapshot>? existingVersions = null)
    {
        manifests ??= CreateManifests();
        PackageIdentifier identifier = manifests.Version.PackageIdentifier!;
        PackageVersion version = manifests.Version.PackageVersion!;
        string directory = ManifestPaths.GetVersionDirectory(identifier, version);
        IReadOnlyDictionary<string, string> files = PackageManifestIO.SerializeFiles(manifests);
        ManifestDocument[] documents =
        [
            .. files.Select(file => new ManifestDocument($"{directory}/{file.Key}", file.Value)),
        ];
        RepositoryFileChange[] changes =
        [
            .. documents.Select(static document =>
                new RepositoryFileChange(document.RepositoryPath, RepositoryChangeKind.Added)),
        ];
        InstallerArtifact[] artifacts =
        [
            .. manifests.Installer.Installers!
                .Where(static installer =>
                    installer.InstallerUrl is not null
                    && installer.InstallerSha256 is not null)
                .DistinctBy(static installer => installer.InstallerUrl, StringComparer.Ordinal)
                .Select(static installer => new InstallerArtifact(
                    installer.InstallerUrl!,
                    CreateDownload(installer.InstallerUrl!, installer.InstallerSha256!))),
        ];
        return new PreflightRequest
        {
            Documents = documents,
            Changes = changes,
            InstallerArtifacts = artifacts,
            ExistingVersions = existingVersions ?? [],
            Options = options ?? new PreflightOptions(),
        };
    }

    public static DownloadResult CreateDownload(string url, Sha256Hash hash)
        => new()
        {
            FilePath = Path.Combine(Path.GetTempPath(), "winmatsch-validation-tests", "setup.exe"),
            FileName = "setup.exe",
            Sha256 = hash,
            SizeInBytes = 42,
            InitialUrl = url,
            FinalUrl = url,
            RetrievedAt = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
        };

    public static DownloadResult CopyDownload(
        DownloadResult source,
        string? filePath = null,
        string? finalUrl = null)
        => new()
        {
            FilePath = filePath ?? source.FilePath,
            FileName = filePath is null ? source.FileName : Path.GetFileName(filePath),
            Sha256 = source.Sha256,
            SizeInBytes = source.SizeInBytes,
            LastModified = source.LastModified,
            ETag = source.ETag,
            ResponseDate = source.ResponseDate,
            FreshUntil = source.FreshUntil,
            RetrievedAt = source.RetrievedAt,
            InitialUrl = source.InitialUrl,
            FinalUrl = finalUrl ?? source.FinalUrl,
            ContentType = source.ContentType,
            IsFromCache = source.IsFromCache,
            MayBeStored = source.MayBeStored,
        };

    public static DownloadResult CopyDownloadForFile(
        DownloadResult source,
        string filePath,
        Sha256Hash? hashOverride = null)
    {
        using FileStream stream = File.OpenRead(filePath);
        var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
        return new DownloadResult
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Sha256 = hashOverride ?? hash,
            SizeInBytes = stream.Length,
            LastModified = source.LastModified,
            ETag = source.ETag,
            ResponseDate = source.ResponseDate,
            FreshUntil = source.FreshUntil,
            RetrievedAt = source.RetrievedAt,
            InitialUrl = source.InitialUrl,
            FinalUrl = source.FinalUrl,
            ContentType = source.ContentType,
            IsFromCache = source.IsFromCache,
            MayBeStored = source.MayBeStored,
        };
    }
}

internal sealed class FakePreflightNetwork : IPreflightNetwork
{
    private readonly List<string>? _events;

    public FakePreflightNetwork(List<string>? events = null)
    {
        _events = events;
    }

    public string? FailingProbeUrl { get; init; }

    public string? InvalidOperationProbeUrl { get; init; }

    public bool ReturnChangedContent { get; init; }

    public string? RevalidatedFinalUrl { get; init; }

    public int ProbeCount { get; private set; }

    public int RevalidationCount { get; private set; }

    public Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCount++;
        _events?.Add($"probe:{url}");
        if (string.Equals(url, FailingProbeUrl, StringComparison.Ordinal))
        {
            throw new DownloadHttpException(System.Net.HttpStatusCode.NotFound, url);
        }

        if (string.Equals(url, InvalidOperationProbeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Insecure HTTP downloads are disabled.");
        }

        return Task.FromResult(new DownloadProbeResult
        {
            InitialUrl = url,
            FinalUrl = url,
            Method = DownloadProbeMethod.Head,
        });
    }

    public Task<DownloadRevalidationResult> RevalidateAsync(
        DownloadResult previous,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevalidationCount++;
        _events?.Add($"revalidate:{previous.InitialUrl}");
        return Task.FromResult(new DownloadRevalidationResult
        {
            Status = ReturnChangedContent
                ? DownloadRevalidationStatus.ContentChanged
                : DownloadRevalidationStatus.Unchanged,
            Result = RevalidatedFinalUrl is null
                ? previous
                : TestPackageFactory.CopyDownload(previous, finalUrl: RevalidatedFinalUrl),
        });
    }
}

internal sealed class FakeBoundary(List<string>? events = null) : IPreflightBoundary
{
    private readonly List<string>? _events = events;

    public int InvocationCount { get; private set; }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        _events?.Add("boundary");
        return Task.CompletedTask;
    }
}
