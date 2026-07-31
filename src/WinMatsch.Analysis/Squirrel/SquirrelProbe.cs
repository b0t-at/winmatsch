using System.Buffers.Binary;
using System.IO.Compression;
using WinMatsch.Analysis.Advanced;
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

    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        bool hasMarker = HasSquirrelMarker(peFile.VersionInfo);
        byte[]? classicPayload = PeResourceReader.Read(stream, ClassicResourceType, ClassicResourceId);
        if (classicPayload is not null)
        {
            using var payload = new MemoryStream(classicPayload, writable: false);
            NuspecMetadata metadata = ReadClassicPayload(payload, out string nupkgName);
            return Compose(peFile, metadata, nupkgName);
        }

        long imageEnd = PeOverlay.GetStart(stream);
        BundleLocation? bundle = FindClowdBundle(stream, imageEnd);
        if (bundle is not null)
        {
            using var package = new SubStream(stream, bundle.Value.Offset, bundle.Value.Length);
            NuspecMetadata metadata = ReadPackage(package, "The Clowd.Squirrel release package");
            return Compose(peFile, metadata, nupkgName: null);
        }

        return hasMarker ? Compose(peFile, metadata: null, nupkgName: null) : null;
    }

    private static NuspecMetadata ReadClassicPayload(Stream payload, out string nupkgName)
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

        nupkgName = Path.GetFileName(nupkg.FullName);
        return ReadNestedPackage(nupkg);
    }

    private static NuspecMetadata ReadNestedPackage(ZipArchiveEntry entry)
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
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "The Squirrel release package is truncated or corrupt.", ex);
        }

        package.Position = 0;
        return ReadPackage(package, "The Squirrel release package");
    }

    private static NuspecMetadata ReadPackage(Stream package, string description)
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

        return ReadNuspec(nuspec);
    }

    private static ZipArchive OpenZip(Stream stream, string description)
    {
        stream.Position = 0;
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{description} is truncated or corrupt.", ex);
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

    private static InstallerAnalysis Compose(PeFile peFile, NuspecMetadata? metadata, string? nupkgName)
    {
        VersionInfo version = peFile.VersionInfo;
        var installer = new Installer
        {
            Architecture = (nupkgName is null ? null : UrlArchitectureDetector.Detect(nupkgName))
                ?? peFile.Architecture,
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
        };
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

    private readonly record struct BundleLocation(long Offset, long Length);
}
