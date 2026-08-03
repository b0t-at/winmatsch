using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Workflows.Diagnostics;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class RepositoryManifestSnapshotSourceTests
{
    [Fact]
    public async Task List_versions_loads_only_the_latest_remote_manifest_without_writing_output()
    {
        string output = Directory.CreateTempSubdirectory(
            "winmatsch-repository-source-test-").FullName;
        PackageVersionResult latest = PackageVersion("1.10");
        var diagnostics = new FakeRepositoryDiagnosticService(latest);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"));
        try
        {
            ImmutableArray<PackageSnapshot> snapshots = await source.ListVersionsAsync(
                output,
                latest.Identifier,
                CancellationToken.None);

            PackageSnapshot snapshot = Assert.Single(snapshots);
            Assert.Equal("1.10", snapshot.PackageVersion.Value);
            Assert.Equal("Example.App", snapshot.Manifests.Version.PackageIdentifier!.Value);
            Assert.Equal(1, diagnostics.ListCalls);
            Assert.Equal(1, diagnostics.GetCalls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Fallback_uses_local_versions_without_querying_the_repository()
    {
        PackageSnapshot local = Snapshot(PackageVersion("1.9"));
        var primary = new SnapshotSource(local);
        var fallback = new SnapshotSource(Snapshot(PackageVersion("1.10")));
        var source = new FallbackManifestSnapshotSource(primary, fallback);

        ImmutableArray<PackageSnapshot> snapshots = await source.ListVersionsAsync(
            ".",
            local.PackageIdentifier,
            CancellationToken.None);

        Assert.Equal("1.9", Assert.Single(snapshots).PackageVersion.Value);
        Assert.Equal(0, fallback.ListCalls);
    }

    [Fact]
    public async Task Loaded_remote_snapshot_is_reused_for_plan_revalidation()
    {
        PackageVersionResult latest = PackageVersion("1.10");
        var diagnostics = new FakeRepositoryDiagnosticService(latest);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"));

        _ = await source.ListVersionsAsync(
            ".",
            latest.Identifier,
            CancellationToken.None);
        _ = await source.LoadAsync(
            ".",
            latest.Identifier,
            latest.Version,
            CancellationToken.None);

        Assert.Equal(1, diagnostics.GetCalls);
    }

    [Fact]
    public async Task Explicit_source_does_not_fetch_target_or_relist_versions()
    {
        PackageVersionResult sourceVersion = PackageVersion("1.10");
        var diagnostics = new FakeRepositoryDiagnosticService(sourceVersion);
        var source = new RepositoryManifestSnapshotSource(
            diagnostics,
            new RepositoryCoordinates("microsoft", "winget-pkgs"),
            sourceVersion.Version);

        PackageSnapshot? target = await source.LoadAsync(
            ".",
            sourceVersion.Identifier,
            new PackageVersion("1.11"),
            CancellationToken.None);
        ImmutableArray<PackageSnapshot> first = await source.ListVersionsAsync(
            ".",
            sourceVersion.Identifier,
            CancellationToken.None);
        ImmutableArray<PackageSnapshot> second = await source.ListVersionsAsync(
            ".",
            sourceVersion.Identifier,
            CancellationToken.None);

        Assert.Null(target);
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(0, diagnostics.ListCalls);
        Assert.Equal(1, diagnostics.GetCalls);
    }

    private static PackageVersionResult PackageVersion(string versionValue)
    {
        var repository = new RepositoryCoordinates("microsoft", "winget-pkgs");
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion(versionValue);
        var locale = new LanguageTag("en-US");
        var manifests = new PackageManifests
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
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                        InstallerUrl = "https://example.test/app.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            Locales = [],
        };
        string directory = ManifestPaths.GetVersionDirectory(identifier, version);
        RepositoryManifestFile[] files =
        [
            .. PackageManifestIO.SerializeFiles(manifests).Select(pair =>
                new RepositoryManifestFile($"{directory}/{pair.Key}", pair.Value)),
        ];
        return new(
            repository,
            "master",
            identifier,
            version,
            Normalized: false,
            files);
    }

    private static PackageSnapshot Snapshot(PackageVersionResult result)
    {
        string directory = Directory.CreateTempSubdirectory(
            "winmatsch-snapshot-test-").FullName;
        try
        {
            foreach (RepositoryManifestFile file in result.Files)
            {
                File.WriteAllText(
                    Path.Combine(directory, Path.GetFileName(file.Path)),
                    file.Content);
            }

            return new()
            {
                PackageIdentifier = result.Identifier,
                PackageVersion = result.Version,
                VersionDirectory = ManifestPaths.GetVersionDirectory(
                    result.Identifier,
                    result.Version),
                Manifests = PackageManifestIO.LoadDirectory(directory),
                Documents =
                [
                    .. result.Files.Select(static file => new RawManifestDocument(
                        file.Path,
                        System.Text.Encoding.UTF8.GetBytes(file.Content))),
                ],
            };
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRepositoryDiagnosticService(
        PackageVersionResult latest) : IRepositoryDiagnosticService
    {
        public int GetCalls { get; private set; }

        public int ListCalls { get; private set; }

        public Task<PackageVersionResult> GetPackageVersionAsync(
            RepositoryCoordinates repository,
            PackageIdentifier identifier,
            PackageVersion version,
            bool normalize,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(latest);
        }

        public Task<PackageVersionsResult> ListVersionsAsync(
            RepositoryCoordinates repository,
            PackageIdentifier identifier,
            int skip,
            int limit,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new PackageVersionsResult(
                repository,
                "master",
                identifier,
                skip,
                limit,
                1,
                [latest.Version]));
        }
    }

    private sealed class SnapshotSource(params PackageSnapshot[] snapshots)
        : IManifestSnapshotSource
    {
        public int ListCalls { get; private set; }

        public Task<PackageSnapshot?> LoadAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            PackageVersion packageVersion,
            CancellationToken cancellationToken)
            => Task.FromResult(snapshots.SingleOrDefault(snapshot =>
                snapshot.PackageVersion.Equals(packageVersion)));

        public Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
            string outputDirectory,
            PackageIdentifier packageIdentifier,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(snapshots.ToImmutableArray());
        }
    }
}
