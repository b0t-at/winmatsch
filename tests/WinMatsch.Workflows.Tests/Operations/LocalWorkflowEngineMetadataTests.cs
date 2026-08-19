using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

/// <summary>
/// Stale-metadata carryover guards: carried release notes are cleared, definitively dead
/// optional metadata URLs are dropped, MSIX signature hashes are recomputed from analysis,
/// and version-embedding install locations are substituted.
/// </summary>
public sealed class LocalWorkflowEngineMetadataTests
{
    [Fact]
    public void Carried_release_notes_are_cleared_from_default_and_per_locale_manifests()
    {
        PackageManifests manifests = CreateManifests();
        manifests.DefaultLocale.ReleaseNotes = "old notes";
        manifests.DefaultLocale.ReleaseNotesUrl = "https://example.test/releases/tag/v1.0.0";
        manifests.Locales.Add(new LocaleManifest
        {
            PackageIdentifier = manifests.Version.PackageIdentifier,
            PackageVersion = manifests.Version.PackageVersion,
            PackageLocale = new LanguageTag("de-DE"),
            ReleaseNotes = "alte Notizen",
            ReleaseNotesUrl = "https://example.test/releases/tag/v1.0.0",
        });

        LocalWorkflowEngine.ClearCarriedReleaseNotes(manifests);

        Assert.Null(manifests.DefaultLocale.ReleaseNotes);
        Assert.Null(manifests.DefaultLocale.ReleaseNotesUrl);
        Assert.Null(manifests.Locales[0].ReleaseNotes);
        Assert.Null(manifests.Locales[0].ReleaseNotesUrl);
    }

    [Fact]
    public void Dead_optional_metadata_urls_are_dropped_from_all_locales()
    {
        PackageManifests manifests = CreateManifests();
        manifests.DefaultLocale.LicenseUrl = "https://example.test/dead/LICENSE";
        manifests.DefaultLocale.PublisherUrl = "https://example.test/alive";
        manifests.Locales.Add(new LocaleManifest
        {
            PackageIdentifier = manifests.Version.PackageIdentifier,
            PackageVersion = manifests.Version.PackageVersion,
            PackageLocale = new LanguageTag("de-DE"),
            ReleaseNotesUrl = "https://example.test/dead/LICENSE",
        });
        var validation = new ValidationReport(
        [
            new ValidationFinding(
                PreflightGate.DeadMetadataUrlCode,
                ValidationSeverity.Warning,
                "Metadata URL is definitively dead at the origin: HTTP 404.",
                "https://example.test/dead/LICENSE"),
        ]);

        ImmutableArray<string> dropped = LocalWorkflowEngine.DropDeadOptionalMetadataUrls(manifests, validation);

        Assert.Equal(["https://example.test/dead/LICENSE"], dropped);
        Assert.Null(manifests.DefaultLocale.LicenseUrl);
        Assert.Null(manifests.Locales[0].ReleaseNotesUrl);
        Assert.Equal("https://example.test/alive", manifests.DefaultLocale.PublisherUrl);
    }

    [Fact]
    public void Without_dead_url_findings_nothing_is_dropped()
    {
        PackageManifests manifests = CreateManifests();
        manifests.DefaultLocale.LicenseUrl = "https://example.test/LICENSE";
        var validation = new ValidationReport(
        [
            new ValidationFinding(
                "VLD5005",
                ValidationSeverity.Warning,
                "Metadata URL probe failed: transient.",
                "https://example.test/LICENSE"),
        ]);

        Assert.Empty(LocalWorkflowEngine.DropDeadOptionalMetadataUrls(manifests, validation));
        Assert.Equal("https://example.test/LICENSE", manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Msix_signature_hash_is_recomputed_from_the_analyzed_artifact()
    {
        var signature = new Sha256Hash("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        PackageManifests manifests = CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Msix;
        manifests.Installer.Installers =
        [
            new Installer
            {
                Architecture = Architecture.X64,
                InstallerUrl = "https://example.test/app.msix",
                InstallerSha256 = new Sha256Hash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
            },
        ];
        ImmutableArray<ArtifactSnapshot>.Builder artifacts = ImmutableArray.CreateBuilder<ArtifactSnapshot>();
        artifacts.Add(CreateSnapshot(
            "https://example.test/app.msix",
            new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.Msix,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Msix,
                        SignatureSha256 = signature,
                    },
                ],
            }));

        LocalWorkflowEngine.ApplyAnalyzedMsixIdentity(manifests.Installer, artifacts);

        Assert.Equal(signature, manifests.Installer.Installers![0].SignatureSha256);
    }

    [Fact]
    public void Non_msix_installers_do_not_receive_signature_hashes()
    {
        PackageManifests manifests = CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Exe;
        manifests.Installer.Installers =
        [
            new Installer
            {
                Architecture = Architecture.X64,
                InstallerUrl = "https://example.test/app.exe",
            },
        ];
        ImmutableArray<ArtifactSnapshot>.Builder artifacts = ImmutableArray.CreateBuilder<ArtifactSnapshot>();
        artifacts.Add(CreateSnapshot(
            "https://example.test/app.exe",
            new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                Installers = [new Installer { InstallerType = InstallerType.Exe }],
            }));

        LocalWorkflowEngine.ApplyAnalyzedMsixIdentity(manifests.Installer, artifacts);

        Assert.Null(manifests.Installer.Installers![0].SignatureSha256);
    }

    [Fact]
    public void Default_install_location_is_version_substituted()
    {
        // Motivating regression: Saturneric.GpgFrontend shipped a DefaultInstallLocation still
        // embedding the previous version 2.1.12 (PR #420295).
        PackageManifests manifests = CreateManifests();
        manifests.Installer.InstallationMetadata = new InstallationMetadata
        {
            DefaultInstallLocation = @"%LOCALAPPDATA%\Programs\GpgFrontend\2.1.12",
        };
        manifests.Installer.Installers =
        [
            new Installer
            {
                Architecture = Architecture.X64,
                InstallerUrl = "https://example.test/app.msix",
                InstallationMetadata = new InstallationMetadata
                {
                    DefaultInstallLocation = @"C:\Program Files\GpgFrontend v2.1.12",
                },
            },
        ];

        LocalWorkflowEngine.SubstituteVersionInInstallLocations(manifests.Installer, "2.1.12", "2.1.13");

        Assert.Equal(
            @"%LOCALAPPDATA%\Programs\GpgFrontend\2.1.13",
            manifests.Installer.InstallationMetadata!.DefaultInstallLocation);
        Assert.Equal(
            @"C:\Program Files\GpgFrontend v2.1.13",
            manifests.Installer.Installers![0].InstallationMetadata!.DefaultInstallLocation);
    }

    [Fact]
    public void Unbounded_version_lookalikes_are_not_substituted()
    {
        PackageManifests manifests = CreateManifests();
        manifests.Installer.InstallationMetadata = new InstallationMetadata
        {
            DefaultInstallLocation = @"C:\Tools\App12.1.128",
        };

        LocalWorkflowEngine.SubstituteVersionInInstallLocations(manifests.Installer, "2.1.12", "2.1.13");

        Assert.Equal(
            @"C:\Tools\App12.1.128",
            manifests.Installer.InstallationMetadata!.DefaultInstallLocation);
    }

    private static PackageManifests CreateManifests()
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion("2.0.0");
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
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "Example App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            Locales = [],
        };
    }

    private static ArtifactSnapshot CreateSnapshot(string url, InstallerAnalysis analysis)
        => new()
        {
            Asset = new DiscoveredAsset
            {
                ReleaseId = 1,
                ReleaseTag = "v2.0.0",
                ReleaseName = "2.0.0",
                ReleaseUri = new Uri("https://example.test/releases/1"),
                IsPrerelease = false,
                AssetId = 1,
                AssetName = Path.GetFileName(new Uri(url).LocalPath),
                DownloadUri = new Uri(url),
                DeclaredContentType = "application/octet-stream",
                DeclaredSize = 1,
                AssetCreatedAt = DateTimeOffset.UnixEpoch,
            },
            Download = new DownloadResult
            {
                FilePath = Path.Combine(Path.GetTempPath(), "winmatsch-tests", "artifact.bin"),
                FileName = Path.GetFileName(new Uri(url).LocalPath),
                Sha256 = new Sha256Hash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                SizeInBytes = 1,
                RetrievedAt = DateTimeOffset.UnixEpoch,
                InitialUrl = url,
                FinalUrl = url,
            },
            Analysis = analysis,
        };
}
