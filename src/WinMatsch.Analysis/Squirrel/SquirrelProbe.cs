using System.Buffers.Binary;
using System.IO.Compression;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>
/// Detects classic Squirrel.Windows setup resources and Clowd.Squirrel package bundles.
/// The outer bootstrapper decides the format, scope, type, and switches; package metadata
/// supplies the per-user Apps &amp; Features identity.
/// </summary>
public sealed class SquirrelProbe : IExeFormatProbe
{
    private static readonly byte[] _clowdBundleSignature =
    [
        0x94, 0xF0, 0xB1, 0x7B, 0x68, 0x93, 0xE0, 0x29,
        0x37, 0xEB, 0x34, 0xEF, 0x53, 0xAA, 0xE7, 0xD4,
        0x2B, 0x54, 0xF5, 0x70, 0x7E, 0xF5, 0xD6, 0xF5,
        0x78, 0x54, 0x98, 0x3E, 0x5E, 0x94, 0xED, 0x7D,
    ];

    private static readonly string[] _markerTokens =
    [
        "SquirrelSetup",
        "Squirrel Setup",
        "Squirrel.Windows",
        "Clowd.Squirrel",
    ];

    private const string ClassicResourceType = "DATA";
    private const int ClassicResourceId = 131;
    private const long MaxNupkgBytes = 256L * 1024 * 1024;
    private const long MaxNuspecBytes = 4L * 1024 * 1024;
    private const long MaxPayloadPeBytes = 64L * 1024 * 1024;
    private const long MaxTotalPayloadPeBytes = 256L * 1024 * 1024;
    private const int MaxPayloadPeEntries = 256;

    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        bool hasMarker = HasSquirrelMarker(peFile.VersionInfo);
        SquirrelPackageInspection? package = InspectPackage(stream);
        if (package is not null)
        {
            return Compose(peFile, package);
        }

        return hasMarker ? Compose(peFile, package: null) : null;
    }

    internal static IReadOnlyList<SquirrelPayloadPe> InspectPayloadPeEvidence(Stream stream)
        => InspectPackage(stream)?.Payloads ?? [];

    private static SquirrelPackageInspection? InspectPackage(Stream stream)
    {
        byte[]? classicPayload = PeResourceReader.Read(stream, ClassicResourceType, ClassicResourceId);
        if (classicPayload is not null)
        {
            using var payload = new MemoryStream(classicPayload, writable: false);
            return ReadClassicPayload(payload);
        }

        long imageEnd = PeOverlay.GetStart(stream);
        BundleLocation? bundle = FindClowdBundle(stream, imageEnd);
        if (bundle is null)
        {
            return null;
        }

        using var package = new SubStream(stream, bundle.Value.Offset, bundle.Value.Length);
        return ReadPackage(package, "The Clowd.Squirrel release package", nupkgName: null);
    }

    private static SquirrelPackageInspection ReadClassicPayload(Stream payload)
    {
        ZipArchiveBounds.Validate(payload, "The classic Squirrel payload resource");
        using var archive = OpenZip(payload, "The classic Squirrel payload resource");
        ZipArchiveEntry? nupkg = archive.Entries.FirstOrDefault(static entry =>
            entry.FullName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
        if (nupkg is null)
        {
            throw new InvalidDataException(
                "The classic Squirrel payload resource contains no release package.");
        }

        return ReadNestedPackage(nupkg);
    }

    private static SquirrelPackageInspection ReadNestedPackage(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxNupkgBytes)
        {
            throw new InvalidDataException("The Squirrel release package exceeds the supported size.");
        }

        using var package = new MemoryStream();
        try
        {
            using Stream source = entry.Open();
            CopyBounded(source, package, MaxNupkgBytes);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "The Squirrel release package is truncated or corrupt.", exception);
        }

        package.Position = 0;
        return ReadPackage(
            package,
            "The Squirrel release package",
            Path.GetFileName(entry.FullName));
    }

    private static SquirrelPackageInspection ReadPackage(
        Stream package,
        string description,
        string? nupkgName)
    {
        ZipArchiveBounds.Validate(package, description);
        using ZipArchive archive = OpenZip(package, description);
        ZipArchiveEntry? nuspec = archive.Entries.FirstOrDefault(static entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
            && !entry.FullName.Contains('/', StringComparison.Ordinal)
            && !entry.FullName.Contains('\\', StringComparison.Ordinal));
        if (nuspec is null)
        {
            throw new InvalidDataException($"{description} has no root nuspec manifest.");
        }

        NuspecMetadata metadata = ReadNuspec(nuspec);
        List<SquirrelPayloadPe> payloads =
            InspectPayloadPes(archive, description, out IReadOnlyList<AnalysisDiagnostic> diagnostics);
        return new SquirrelPackageInspection(metadata, nupkgName, payloads, diagnostics);
    }

    private static List<SquirrelPayloadPe> InspectPayloadPes(
        ZipArchive archive,
        string description,
        out IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        var payloads = new List<SquirrelPayloadPe>();
        var findings = new List<AnalysisDiagnostic>();
        long totalBytes = 0;
        int candidates = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')
                || (!entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (++candidates > MaxPayloadPeEntries)
            {
                findings.Add(new AnalysisDiagnostic(
                    "SQUIRREL002",
                    $"{description} contains more than {MaxPayloadPeEntries} PE payload candidates; remaining architecture evidence was skipped.",
                    RequiresManualAnalysis: true));
                break;
            }

            using Stream source = entry.Open();
            using var content = new MemoryStream();
            if (!CopyPayloadBounded(
                    source,
                    content,
                    MaxPayloadPeBytes,
                    MaxTotalPayloadPeBytes,
                    ref totalBytes))
            {
                findings.Add(new AnalysisDiagnostic(
                    "SQUIRREL002",
                    $"Squirrel payload '{entry.FullName}' exceeded the bounded PE inspection budget; its architecture is unavailable.",
                    RequiresManualAnalysis: true));
                continue;
            }

            content.Position = 0;
            PeImportInspection inspection = PeImportReader.Inspect(
                content,
                PayloadDependencyAnalyzerOptions.DefaultMaximumImportDescriptors,
                PayloadDependencyAnalyzerOptions.DefaultMaximumImportNameBytes);
            if (inspection.Architecture is { } architecture)
            {
                payloads.Add(new SquirrelPayloadPe(
                    NormalizePackagePath(entry.FullName),
                    content.Length,
                    architecture,
                    inspection));
            }
        }

        diagnostics = findings;
        return payloads;
    }

    private static ZipArchive OpenZip(Stream stream, string description)
    {
        stream.Position = 0;
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{description} is truncated or corrupt.", exception);
        }
    }

    private static NuspecMetadata ReadNuspec(ZipArchiveEntry nuspecEntry)
    {
        if (nuspecEntry.Length > MaxNuspecBytes)
        {
            throw new InvalidDataException("The package's nuspec manifest exceeds the supported size.");
        }

        using var buffer = new MemoryStream();
        using (Stream entryStream = nuspecEntry.Open())
        {
            CopyBounded(entryStream, buffer, MaxNuspecBytes);
        }

        buffer.Position = 0;
        return NuspecReader.Parse(buffer);
    }

    private static BundleLocation? FindClowdBundle(Stream stream, long imageEnd)
    {
        if (imageEnd <= 16 || imageEnd > stream.Length)
        {
            return null;
        }

        const int blockSize = 64 * 1024;
        byte[] block = new byte[blockSize + _clowdBundleSignature.Length - 1];
        int carry = 0;
        long absolute = 0;
        while (absolute < imageEnd)
        {
            int requested = (int)Math.Min(blockSize, imageEnd - absolute);
            stream.Position = absolute;
            int read = stream.ReadAtLeast(block.AsSpan(carry, requested), requested, throwOnEndOfStream: false);
            if (read != requested)
            {
                return null;
            }

            int available = carry + read;
            int index = block.AsSpan(0, available).IndexOf(_clowdBundleSignature);
            if (index >= 0)
            {
                long signatureOffset = absolute - carry + index;
                if (signatureOffset < 16)
                {
                    throw new InvalidDataException(
                        "The Clowd.Squirrel bundle locator is truncated.");
                }

                Span<byte> locator = stackalloc byte[16];
                stream.Position = signatureOffset - locator.Length;
                stream.ReadExactly(locator);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(locator);
                long length = BinaryPrimitives.ReadInt64LittleEndian(locator[8..]);
                if (offset <= 0
                    || length <= 0
                    || offset < imageEnd
                    || offset > stream.Length
                    || length > stream.Length - offset
                    || length > MaxNupkgBytes)
                {
                    throw new InvalidDataException(
                        "The Clowd.Squirrel bundle locator contains an invalid package offset or length.");
                }

                return new BundleLocation(offset, length);
            }

            carry = Math.Min(_clowdBundleSignature.Length - 1, available);
            block.AsSpan(available - carry, carry).CopyTo(block);
            absolute += read;
        }

        return null;
    }

    private static InstallerAnalysis Compose(PeFile peFile, SquirrelPackageInspection? package)
    {
        VersionInfo version = peFile.VersionInfo;
        ArchitectureDecision architecture = DetermineArchitecture(package);
        NuspecMetadata? metadata = package?.Metadata;
        var installer = new Installer
        {
            Architecture = architecture.Architecture,
            InstallerType = InstallerType.Exe,
            Scope = Scope.User,
            ElevationRequirement = peFile.RequestedElevation,
            ProductCode = metadata?.Id,
            InstallerSwitches = new InstallerSwitches
            {
                Silent = "--silent",
                SilentWithProgress = "--silent",
            },
        };

        if (metadata is { HasAnyValue: true })
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = metadata.Title ?? metadata.Id,
                    Publisher = metadata.Authors,
                    DisplayVersion = metadata.Version,
                    ProductCode = metadata.Id,
                },
            ];
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Squirrel,
            Installers = [installer],
            ProductName = metadata?.Title ?? metadata?.Id ?? version.ProductName,
            Publisher = metadata?.Authors ?? version.CompanyName,
            ProductVersion = metadata?.Version ?? version.ProductVersion,
            Copyright = version.LegalCopyright,
            Diagnostics =
            [
                .. (package?.Diagnostics ?? []),
                .. (architecture.Diagnostic is null
                    ? Array.Empty<AnalysisDiagnostic>()
                    : [architecture.Diagnostic]),
            ],
        };
    }

    private static ArchitectureDecision DetermineArchitecture(SquirrelPackageInspection? package)
    {
        Architecture? nameArchitecture = package?.NupkgName is null
            ? null
            : UrlArchitectureDetector.Detect(package.NupkgName);
        SquirrelPayloadPe[] payloads = [.. (package?.Payloads ?? [])];
        Architecture[] payloadArchitectures =
            [.. payloads.Select(static payload => payload.Architecture).Distinct()];
        if (nameArchitecture is { } named)
        {
            if (payloadArchitectures.Length == 0
                || payloadArchitectures.All(architecture => architecture == named))
            {
                return new ArchitectureDecision(named, null);
            }

            return new ArchitectureDecision(
                null,
                new AnalysisDiagnostic(
                    "SQUIRREL001",
                    $"The Squirrel package name implies {named}, but bounded nupkg PE inspection found {string.Join(", ", payloadArchitectures)}.",
                    RequiresManualAnalysis: true));
        }

        if (payloadArchitectures.Length == 1)
        {
            return new ArchitectureDecision(payloadArchitectures[0], null);
        }

        if (payloadArchitectures.Length > 1)
        {
            (Architecture Architecture, long Total, long Largest)[] weights =
            [
                .. payloads
                    .GroupBy(static payload => payload.Architecture)
                    .Select(static group => (
                        Architecture: group.Key,
                        Total: group.Sum(static payload => payload.Size),
                        Largest: group.Max(static payload => payload.Size)))
                    .OrderByDescending(static item => item.Total)
                    .ThenByDescending(static item => item.Largest),
            ];
            if (weights[0].Total >= weights[1].Total * 2
                && weights[0].Largest > weights[1].Largest)
            {
                return new ArchitectureDecision(
                    weights[0].Architecture,
                    new AnalysisDiagnostic(
                        "SQUIRREL001",
                        $"The Squirrel nupkg contains mixed PE architectures; {weights[0].Architecture} was selected from dominant payload size evidence.",
                        RequiresManualAnalysis: true));
            }

            return new ArchitectureDecision(
                null,
                new AnalysisDiagnostic(
                    "SQUIRREL001",
                    $"The Squirrel nupkg contains mixed PE architectures ({string.Join(", ", payloadArchitectures)}); no architecture was selected.",
                    RequiresManualAnalysis: true));
        }

        return new ArchitectureDecision(
            null,
            new AnalysisDiagnostic(
                "SQUIRREL001",
                "No bounded nupkg PE architecture evidence was available; the outer Squirrel bootstrap stub was not treated as the installed payload.",
                RequiresManualAnalysis: true));
    }

    private static bool HasSquirrelMarker(VersionInfo version)
        => ContainsMarker(version.FileDescription)
            || ContainsMarker(version.ProductName)
            || ContainsMarker(version.CompanyName)
            || ContainsMarker(version.OriginalFilename);

    private static bool ContainsMarker(string? value)
        => value is not null
            && _markerTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void CopyBounded(Stream source, Stream destination, long maxBytes)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("The embedded payload expands beyond the supported bound.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static bool CopyPayloadBounded(
        Stream source,
        Stream destination,
        long maxPayloadBytes,
        long maxTotalBytes,
        ref long totalBytes)
    {
        byte[] buffer = new byte[81920];
        long payloadBytes = 0;
        while (true)
        {
            int allowed = (int)Math.Min(
                buffer.Length,
                Math.Min(maxPayloadBytes - payloadBytes + 1, maxTotalBytes - totalBytes + 1));
            if (allowed <= 0)
            {
                return false;
            }

            int read = source.Read(buffer, 0, allowed);
            if (read == 0)
            {
                return true;
            }

            payloadBytes += read;
            totalBytes += read;
            if (payloadBytes > maxPayloadBytes || totalBytes > maxTotalBytes)
            {
                return false;
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static string NormalizePackagePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"The Squirrel package entry '{path}' has an unsafe path.");
        }

        return normalized;
    }

    private readonly record struct BundleLocation(long Offset, long Length);

    private sealed record SquirrelPackageInspection(
        NuspecMetadata Metadata,
        string? NupkgName,
        IReadOnlyList<SquirrelPayloadPe> Payloads,
        IReadOnlyList<AnalysisDiagnostic> Diagnostics);

    private sealed record ArchitectureDecision(
        Architecture? Architecture,
        AnalysisDiagnostic? Diagnostic);
}

internal sealed record SquirrelPayloadPe(
    string Path,
    long Size,
    Architecture Architecture,
    PeImportInspection ImportInspection);
