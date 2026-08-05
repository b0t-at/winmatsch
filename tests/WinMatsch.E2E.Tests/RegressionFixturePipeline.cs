using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Analysis.Tests;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.Testing.Fixtures;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.E2E.Tests;

internal static class RegressionFixturePipeline
{
    public static IReadOnlyDictionary<string, byte[]> BuildAssets(RegressionFixture fixture)
        => fixture.Descriptor.Assets.ToDictionary(
            static asset => asset.Url.AbsoluteUri,
            asset => BuildAsset(fixture, asset),
            StringComparer.Ordinal);

    public static RegressionFixtureRun CreateEngine(
        RegressionFixture fixture,
        IReadOnlyDictionary<string, byte[]> assets)
    {
        var overrideStore = new TemporaryDirectory("regression-overrides");
        var handler = new FixtureHttpMessageHandler(fixture.Descriptor, assets);
        var releaseSource = new FixtureReleaseSource(fixture.Descriptor, assets);
        var downloader = new InstallerDownloader(handler);
        try
        {
            LocalWorkflowEngine engine = WorkflowProductionComposition.CreateLocalEngine(
                downloader,
                releaseSource,
                new FixtureClock(fixture.Descriptor.Provenance.ObservedAt),
                new OverridePackStoreOptions { RootDirectory = overrideStore.Path });
            return new(engine, handler, releaseSource, overrideStore);
        }
        catch
        {
            overrideStore.Dispose();
            throw;
        }
    }

    public static LocalWorkflowEngine CreateEngineWithOverrideStore(
        RegressionFixture fixture,
        IReadOnlyDictionary<string, byte[]> assets,
        OverridePackStoreOptions overrideStore)
    {
        var handler = new FixtureHttpMessageHandler(fixture.Descriptor, assets);
        var releaseSource = new FixtureReleaseSource(fixture.Descriptor, assets);
        return WorkflowProductionComposition.CreateLocalEngine(
            new InstallerDownloader(handler),
            releaseSource,
            new FixtureClock(fixture.Descriptor.Provenance.ObservedAt),
            overrideStore);
    }

    public static WorkflowOperationRequest CreateRequest(RegressionFixture fixture, string outputDirectory)
    {
        FixtureDescriptor descriptor = fixture.Descriptor;
        FixtureScenario scenario = descriptor.Scenario;
        ImmutableArray<UrlOverride> overrides =
        [
            .. descriptor.Assets
                .Where(static asset => asset.Synthetic.ExplicitArchitecture is not null)
                .Select(asset => new UrlOverride(
                    asset.Url,
                    FixtureSemantics.ParseArchitecture(asset.Synthetic.ExplicitArchitecture!),
                    null,
                    null)),
        ];
        var release = new ReleaseRequest($"v{descriptor.Package.Version}", [], []);
        return scenario.Operation.Equals("update", StringComparison.OrdinalIgnoreCase)
            ? new UpdateOperationRequest
            {
                OutputDirectory = outputDirectory,
                PackageIdentifier = new PackageIdentifier(descriptor.Package.Identifier),
                PreviousVersion = new PackageVersion(
                    scenario.PreviousVersion
                        ?? throw new InvalidDataException($"Fixture '{descriptor.Id}' has no previous version.")),
                PackageVersion = descriptor.Package.Version,
                Release = release,
                UrlOverrides = overrides,
                AllowStableUrlContentChange = true,
                NetworkValidationMode = NetworkValidationMode.Online,
                ApproveReview = scenario.ApproveReview,
                CreatedWith = "winmatsch synthetic regression fixture",
            }
            : new NewOperationRequest
            {
                OutputDirectory = outputDirectory,
                PackageIdentifier = new PackageIdentifier(descriptor.Package.Identifier),
                PackageVersion = descriptor.Package.Version,
                Release = release,
                Locale = CreateLocale(descriptor),
                UrlOverrides = overrides,
                NetworkValidationMode = NetworkValidationMode.Online,
                ApproveReview = scenario.ApproveReview,
                CreatedWith = "winmatsch synthetic regression fixture",
            };
    }

    public static void WritePreviousManifests(RegressionFixture fixture, string outputDirectory)
    {
        FixtureScenario scenario = fixture.Descriptor.Scenario;
        if (!scenario.Operation.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PackageIdentifier identifier = new(fixture.Descriptor.Package.Identifier);
        PackageVersion version = new(
            scenario.PreviousVersion
                ?? throw new InvalidDataException($"Fixture '{fixture.Descriptor.Id}' has no previous version."));
        LanguageTag locale = new(scenario.Locale.PackageLocale);
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
                    .. scenario.PreviousInstallers.Select(previous =>
                        CreatePreviousInstaller(fixture.Descriptor, previous)),
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = scenario.Locale.Publisher,
                PackageName = scenario.Locale.PackageName ?? fixture.Descriptor.Package.Identifier,
                License = scenario.Locale.License,
                ShortDescription = scenario.Locale.ShortDescription
                    ?? $"Synthetic regression fixture for {fixture.Descriptor.Id}.",
            },
            Locales = [],
        };
        string directory = Path.Combine(
            outputDirectory,
            ManifestPaths.GetVersionDirectory(identifier, version)
                .Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach ((string fileName, string content) in PackageManifestIO.SerializeFiles(
                     manifests,
                     new ManifestWriteOptions { CreatedWith = "independently encoded previous fixture" }))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
        }
    }

    public static string Describe(WorkflowOperationResult result)
        => string.Join(
            Environment.NewLine,
            [
                $"Code: {result.Code}",
                $"Error: {result.ErrorMessage}",
                .. result.Plan.Questions.Select(static question =>
                    $"{question.Code}: {question.Prompt} ({question.Path})"),
                .. result.Plan.Validation.Findings.Select(static finding =>
                    $"{finding.Code}: {finding.Message} ({finding.Path})"),
            ]);

    private static byte[] BuildAsset(RegressionFixture fixture, FixtureAsset asset)
    {
        Architecture architecture = FixtureSemantics.ParseArchitecture(asset.Synthetic.Architecture);
        string kind = asset.Synthetic.Kind;
        byte[] bytes = kind.ToLowerInvariant() switch
        {
            "portable" => DependencyFixtures.BuildPe(
                ToMachine(architecture),
                [.. (asset.Synthetic.Imports ?? [])]),
            "nullsoft" => BuildNullsoft(fixture.Descriptor, asset, architecture),
            "inno" => BuildInno(fixture.Descriptor, asset, architecture),
            "wix" or "msi" => BuildMsi(fixture.Descriptor, architecture, kind),
            "zip" => BuildZip(fixture.Descriptor, asset, architecture),
            "msix" => MsixFixtures.BuildPackage(MsixFixtures.PackageManifest(
                identityName: fixture.Descriptor.Package.Identifier,
                version: ToMsixVersion(fixture.Descriptor.Package.Version),
                processorArchitecture: ArchitectureToken(architecture),
                displayName: fixture.Descriptor.Package.Identifier)).ToArray(),
            _ => throw new InvalidDataException(
                $"Fixture '{fixture.Descriptor.Id}' asset '{asset.FileName}' has unknown synthetic kind '{kind}'."),
        };
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(asset.SyntheticSha256, StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("WINMATSCH_UPDATE_REGRESSION_GOLDENS") != "1")
        {
            throw new InvalidDataException(
                $"Fixture '{fixture.Descriptor.Id}' asset '{asset.FileName}' synthetic SHA-256 is '{actual}', "
                + $"not the pinned '{asset.SyntheticSha256}'.");
        }

        return bytes;
    }

    private static byte[] BuildNullsoft(
        FixtureDescriptor descriptor,
        FixtureAsset asset,
        Architecture architecture)
    {
        IReadOnlyList<string> configuredPayloadArchitectures =
            asset.Synthetic.PayloadArchitectures ?? [];
        List<string> payloadArchitectures = configuredPayloadArchitectures.Count > 0
            ? [.. configuredPayloadArchitectures]
            : [asset.Synthetic.Architecture];
        var options = new NsisFixtures.Options
        {
            LangName = descriptor.Package.Identifier,
            Version = new VersionStrings(
                ProductName: descriptor.Package.Identifier,
                CompanyName: descriptor.Scenario.Locale.Publisher,
                ProductVersion: descriptor.Package.Version),
            InstallDirectory = architecture == Architecture.X64
                ? [NsisFixtures.Token.ShellProgramFiles(x64: true), NsisFixtures.Token.Lit(@"\WinMatschFixture")]
                : [NsisFixtures.Token.ShellProgramFiles(x64: false), NsisFixtures.Token.Lit(@"\WinMatschFixture")],
        };
        foreach (string payloadArchitecture in payloadArchitectures)
        {
            options.PayloadNames.Add(payloadArchitecture.ToLowerInvariant() switch
            {
                "x86" => "app-ia32.7z",
                "x64" => "app-x64.7z",
                "arm64" => "app-arm64.7z",
                _ => throw new InvalidDataException(
                    $"Fixture '{descriptor.Id}' has unknown NSIS payload architecture '{payloadArchitecture}'."),
            });
        }

        return NsisFixtures.BuildInstaller(options);
    }

    private static byte[] BuildInno(
        FixtureDescriptor descriptor,
        FixtureAsset asset,
        Architecture architecture)
    {
        var options = new InnoFixtures.Options
        {
            AppName = descriptor.Package.Identifier,
            AppVerName = $"{descriptor.Package.Identifier} {descriptor.Package.Version}",
            AppVersion = descriptor.Package.Version,
            Publisher = descriptor.Scenario.Locale.Publisher,
            UninstallDisplayName = $"{descriptor.Package.Identifier} {descriptor.Package.Version}",
            ArchitecturesAllowed = asset.Synthetic.ArchitectureExpression ?? architecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64compatible",
                Architecture.Arm64 => "arm64",
                _ => "x86compatible",
            },
            ArchitecturesInstallIn64BitMode = architecture == Architecture.X86 ? "" : "x64compatible",
            PrivilegeOverrides = descriptor.Scenario.Operation.Equals(
                "update",
                StringComparison.OrdinalIgnoreCase)
                ? (byte)1
                : (byte)0,
            Languages = descriptor.Scenario.Operation.Equals(
                "update",
                StringComparison.OrdinalIgnoreCase)
                ? []
                : [new InnoFixtures.Language("english", 1033)],
            PayloadMachines =
            [
                .. (asset.Synthetic.PayloadArchitectures ?? []).Select(value =>
                    ToMachine(FixtureSemantics.ParseArchitecture(value))),
            ],
        };
        return InnoFixtures.BuildInstaller(options);
    }

    private static byte[] BuildMsi(
        FixtureDescriptor descriptor,
        Architecture architecture,
        string kind)
    {
        byte[] normalized = Create();
        NormalizeCompoundFileMetadata(normalized);
        return normalized;

        byte[] Create() => MsiFixtures.BuildMsi(
        [
            ("ProductName", descriptor.Package.Identifier),
            ("ProductVersion", descriptor.Package.Version),
            ("Manufacturer", descriptor.Scenario.Locale.Publisher),
            ("ProductCode", "{11111111-2222-3333-4444-555555555555}"),
            ("ALLUSERS", "2"),
        ],
        template: $"{MsiArchitectureToken(architecture)};1033",
        creatingApplication: kind.Equals("wix", StringComparison.OrdinalIgnoreCase)
            ? "WiX Toolset v4"
            : "Windows Installer");
    }

    private static void NormalizeCompoundFileMetadata(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        if (bytes.Length < 512 || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new InvalidDataException("Synthetic MSI is not a Compound File Binary document.");
        }

        bytes.AsSpan(8, 16).Clear();
        bytes.AsSpan(52, 4).Clear();
        int sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(30, 2));
        int sectorCount = (bytes.Length - 512) / sectorSize;
        var fat = new uint[sectorCount];
        int fatOffset = 0;
        for (int index = 0; index < 109; index++)
        {
            uint fatSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76 + (index * 4), 4));
            if (fatSector >= sectorCount)
            {
                continue;
            }

            int offset = SectorOffset(fatSector);
            for (int entry = 0; entry < sectorSize / 4 && fatOffset < fat.Length; entry++)
            {
                fat[fatOffset++] = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(offset + (entry * 4), 4));
            }
        }

        uint directorySector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48, 4));
        var visited = new HashSet<uint>();
        while (directorySector < sectorCount && visited.Add(directorySector))
        {
            int offset = SectorOffset(directorySector);
            for (int entry = 0; entry < sectorSize / 128; entry++)
            {
                Span<byte> directoryEntry = bytes.AsSpan(offset + (entry * 128), 128);
                directoryEntry.Slice(80, 36).Clear();
            }

            directorySector = fat[directorySector];
        }

        int SectorOffset(uint sector) => checked(512 + ((int)sector * sectorSize));
    }

    private static byte[] BuildZip(
        FixtureDescriptor descriptor,
        FixtureAsset asset,
        Architecture architecture)
    {
        IReadOnlyList<string> nestedPayloadPaths = asset.Synthetic.NestedPayloadPaths ?? [];
        if (nestedPayloadPaths.Count == 0)
        {
            throw new InvalidDataException(
                $"Fixture '{descriptor.Id}' ZIP asset '{asset.FileName}' has no synthetic nested payload paths.");
        }

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in nestedPayloadPaths)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using Stream destination = entry.Open();
                destination.Write(DependencyFixtures.BuildPe(
                    ToMachine(architecture),
                    [.. (asset.Synthetic.Imports ?? [])]));
            }
        }

        byte[] bytes = stream.ToArray();
        MsixFixtures.NormalizeZipMetadata(bytes);
        return bytes;
    }

    private static Installer CreatePreviousInstaller(
        FixtureDescriptor descriptor,
        FixturePreviousInstaller previous)
    {
        FixtureAsset asset = descriptor.Assets.Single(
            candidate => candidate.FileName.Equals(previous.AssetFileName, StringComparison.Ordinal));
        var installer = new Installer
        {
            Architecture = FixtureSemantics.ParseArchitecture(previous.Architecture),
            InstallerType = FixtureSemantics.ParseInstallerType(previous.InstallerType),
            NestedInstallerType = previous.NestedInstallerType is null
                ? null
                : FixtureSemantics.ParseInstallerType(previous.NestedInstallerType),
            Scope = FixtureSemantics.ParseScope(previous.Scope),
            InstallerUrl = asset.Url.AbsoluteUri,
            InstallerSha256 = new Sha256Hash(asset.UpstreamSha256),
            InstallerSwitches = previous.CustomSwitch is null
                ? null
                : new InstallerSwitches { Custom = previous.CustomSwitch },
            NestedInstallerFiles =
            [
                .. (previous.NestedInstallerFiles ?? []).Select(static path =>
                    new NestedInstallerFile { RelativeFilePath = path }),
            ],
        };
        if (previous.DisplayName is not null
            || previous.DisplayVersion is not null
            || previous.ProductCode is not null)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = previous.DisplayName,
                    DisplayVersion = previous.DisplayVersion,
                    ProductCode = previous.ProductCode,
                },
            ];
        }

        IReadOnlyList<string> packageDependencies = previous.PackageDependencies ?? [];
        if (packageDependencies.Count > 0)
        {
            installer.Dependencies = new Dependencies
            {
                PackageDependencies =
                [
                    .. packageDependencies.Select(static identifier =>
                        new PackageDependency { PackageIdentifier = new PackageIdentifier(identifier) }),
                ],
            };
        }

        return installer;
    }

    private static PackageLocaleMetadata CreateLocale(FixtureDescriptor descriptor)
    {
        FixtureLocale locale = descriptor.Scenario.Locale;
        return new()
        {
            PackageLocale = new LanguageTag(locale.PackageLocale),
            Publisher = locale.Publisher,
            PackageName = locale.PackageName ?? descriptor.Package.Identifier,
            License = locale.License,
            ShortDescription = locale.ShortDescription
                ?? $"Synthetic regression fixture for {descriptor.Id}.",
            ReleaseNotes = locale.ReleaseNotes,
            ReleaseNotesUrl = locale.ReleaseNotesUrl,
        };
    }

    private static Machine ToMachine(Architecture architecture)
        => architecture switch
        {
            Architecture.X86 => Machine.I386,
            Architecture.X64 => Machine.Amd64,
            Architecture.Arm => Machine.ArmThumb2,
            Architecture.Arm64 => Machine.Arm64,
            _ => Machine.I386,
        };

    private static string ArchitectureToken(Architecture architecture)
        => architecture.ToString().ToLowerInvariant();

    private static string MsiArchitectureToken(Architecture architecture)
        => architecture switch
        {
            Architecture.X86 => "Intel",
            Architecture.X64 => "x64",
            Architecture.Arm => "Arm",
            Architecture.Arm64 => "Arm64",
            _ => "",
        };

    private static string ToMsixVersion(string value)
    {
        string[] parts = value.Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", 4)).Take(4));
    }

    private sealed class FixtureClock(DateTimeOffset value) : IWorkflowClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }
}

internal sealed class RegressionFixtureRun : IDisposable
{
    private readonly TemporaryDirectory _overrideStore;

    public RegressionFixtureRun(
        LocalWorkflowEngine engine,
        FixtureHttpMessageHandler handler,
        FixtureReleaseSource releaseSource,
        TemporaryDirectory overrideStore)
    {
        Engine = engine;
        Handler = handler;
        ReleaseSource = releaseSource;
        _overrideStore = overrideStore;
    }

    public LocalWorkflowEngine Engine { get; }

    public FixtureHttpMessageHandler Handler { get; }

    public FixtureReleaseSource ReleaseSource { get; }

    public string OverrideStorePath => _overrideStore.Path;

    public void Dispose() => _overrideStore.Dispose();
}

internal sealed class FixtureReleaseSource(
    FixtureDescriptor descriptor,
    IReadOnlyDictionary<string, byte[]> assets) : IWorkflowReleaseSource
{
    private readonly FixtureDescriptor _descriptor = descriptor;
    private readonly IReadOnlyDictionary<string, byte[]> _assets = assets;

    public int DiscoveredCount { get; private set; }

    public Task<WorkflowReleaseAssets> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var release = new GitHubRelease(
            1,
            $"v{_descriptor.Package.Version}",
            _descriptor.Package.Version,
            _descriptor.Regression.Summary,
            new Uri($"https://fixtures.invalid/{_descriptor.Id}/releases/v{_descriptor.Package.Version}"),
            false,
            false,
            _descriptor.Provenance.ObservedAt,
            [
                .. _descriptor.Assets.Select((asset, index) => new ReleaseAsset(
                    index + 1,
                    asset.FileName,
                    asset.Url,
                    "application/octet-stream",
                    _assets[asset.Url.AbsoluteUri].Length,
                    0,
                    _descriptor.Provenance.ObservedAt,
                    _descriptor.Provenance.ObservedAt)),
            ],
            _descriptor.Provenance.ObservedAt);
        ImmutableArray<DiscoveredAsset> discovered = ReleaseAssetDiscovery.Discover([release]);
        DiscoveredCount = discovered.Length;
        return Task.FromResult(new WorkflowReleaseAssets(discovered, []));
    }
}

internal sealed class FixtureHttpMessageHandler(
    FixtureDescriptor descriptor,
    IReadOnlyDictionary<string, byte[]> assets) : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, byte[]> _assets = assets;
    private readonly HashSet<string> _allowedUrls =
    [
        .. assets.Keys,
        .. new[] { descriptor.Scenario.Locale.ReleaseNotesUrl }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => new Uri(value!, UriKind.Absolute).AbsoluteUri),
    ];

    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri uri = request.RequestUri
            ?? throw new InvalidOperationException("Fixture requests require an absolute URI.");
        Requests.Add((request.Method, uri));
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            throw new InvalidOperationException(
                $"Regression fixtures forbid mutating HTTP method {request.Method}.");
        }

        if (!_allowedUrls.Contains(uri.AbsoluteUri))
        {
            throw new InvalidOperationException(
                $"Regression fixture has no registered response for {uri.AbsoluteUri}.");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(
                _assets.TryGetValue(uri.AbsoluteUri, out byte[]? bytes) ? bytes : []),
            Headers = { ETag = new($"\"{Convert.ToHexString(SHA256.HashData(
                _assets.TryGetValue(uri.AbsoluteUri, out byte[]? etagBytes) ? etagBytes : []))}\"") },
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return Task.FromResult(response);
    }
}
