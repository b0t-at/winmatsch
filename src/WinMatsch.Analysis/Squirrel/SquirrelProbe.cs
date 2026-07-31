using System.IO.Compression;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>
/// Detects Squirrel.Windows and Clowd.Squirrel setup bootstrappers: executables whose PE
/// overlay carries a zip payload — either a zip wrapping the release <c>.nupkg</c> (classic
/// Squirrel <c>Setup.exe</c>) or the nupkg itself appended directly (Clowd.Squirrel) — or
/// whose version strings carry the Squirrel bootstrap branding when the payload is packaged
/// elsewhere (resource-embedded variants). The <c>.nuspec</c> inside the release package is
/// the identity truth: Squirrel creates its Apps &amp; Features entry from the package id,
/// version and authors under <c>HKCU\...\Uninstall\&lt;id&gt;</c>. Squirrel (and every
/// Electron app shipping it) installs to <c>%LocalAppData%</c> without elevation, so the
/// scope is always per-user and the classification is always an EXE bootstrapper — never the
/// format of anything found inside the payload. A payload-less "portable" twin of the same
/// application returns null: only bootstrap evidence claims the format.
/// </summary>
public sealed class SquirrelProbe : IExeFormatProbe
{
    /// <summary>Zip local file header signature: <c>PK\x03\x04</c>.</summary>
    private static readonly byte[] _zipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>Version-info tokens that positively identify a Squirrel bootstrap stub.</summary>
    private static readonly string[] _markerTokens =
    [
        "SquirrelSetup",
        "Squirrel Setup",
        "Squirrel.Windows",
        "Clowd.Squirrel",
    ];

    /// <summary>Overlay window scanned for the zip signature.</summary>
    private const int MaxSignatureScanBytes = 1024 * 1024;

    /// <summary>Upper bound on a nested nupkg copied to memory; larger payloads degrade to stub metadata.</summary>
    private const long MaxNupkgBytes = 256L * 1024 * 1024;

    /// <summary>Upper bound on the nuspec manifest read from the package.</summary>
    private const long MaxNuspecBytes = 4L * 1024 * 1024;

    /// <summary>Upper bound on zip entries inspected while looking for the release package.</summary>
    private const int MaxEntriesScanned = 65536;

    /// <summary>
    /// Returns the installer's analysis, or null when the executable carries neither a
    /// Squirrel release payload nor the bootstrap branding.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is positively a Squirrel bootstrapper (branding marker or a <c>.nupkg</c>
    /// payload entry) but its zip container, release package, or nuspec manifest is
    /// truncated, corrupt, or malformed.
    /// </exception>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        bool hasMarker = HasSquirrelMarker(peFile.VersionInfo);

        long overlayStart = PeOverlay.GetStart(stream);
        long zipStart = overlayStart > 0
            ? PeOverlay.FindSignature(stream, overlayStart, _zipSignature, MaxSignatureScanBytes)
            : -1;
        if (zipStart < 0)
        {
            // No zip payload: a branded bootstrapper still claims (payload packaged
            // elsewhere); anything else — e.g. the portable twin of the app — does not.
            return hasMarker ? Compose(peFile, metadata: null, nupkgName: null) : null;
        }

        var view = new SubStream(stream, zipStart, stream.Length - zipStart);
        ZipArchive payload;
        try
        {
            payload = new ZipArchive(view, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            if (hasMarker)
            {
                throw new InvalidDataException(
                    "The file is a Squirrel bootstrapper, but its payload archive is truncated or corrupt.");
            }

            return null;
        }

        using (payload)
        {
            (ZipArchiveEntry? nupkgEntry, ZipArchiveEntry? rootNuspec) = FindPayloadEntries(payload);

            if (nupkgEntry is not null)
            {
                // Classic Squirrel: Setup.exe overlay is a zip wrapping the release nupkg.
                NuspecMetadata? metadata = ReadNestedNupkg(nupkgEntry);
                return Compose(peFile, metadata, Path.GetFileName(nupkgEntry.FullName));
            }

            if (rootNuspec is not null)
            {
                // Clowd.Squirrel: the overlay zip is the release nupkg itself.
                NuspecMetadata metadata = ReadNuspec(rootNuspec);
                return Compose(peFile, metadata, nupkgName: null);
            }

            return hasMarker ? Compose(peFile, metadata: null, nupkgName: null) : null;
        }
    }

    /// <summary>
    /// Scans the overlay zip (bounded) for the release <c>.nupkg</c> entry or, failing that,
    /// a root-level <c>.nuspec</c> marking the overlay as a nupkg itself.
    /// </summary>
    private static (ZipArchiveEntry? NupkgEntry, ZipArchiveEntry? RootNuspec) FindPayloadEntries(ZipArchive payload)
    {
        ZipArchiveEntry? nupkgEntry = null;
        ZipArchiveEntry? rootNuspec = null;
        int scanned = 0;
        foreach (ZipArchiveEntry entry in payload.Entries)
        {
            if (++scanned > MaxEntriesScanned)
            {
                break;
            }

            if (nupkgEntry is null && entry.FullName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                nupkgEntry = entry;
                break;
            }

            if (rootNuspec is null
                && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains('/', StringComparison.Ordinal))
            {
                rootNuspec = entry;
            }
        }

        return (nupkgEntry, rootNuspec);
    }

    /// <summary>
    /// Copies the nested release package into memory (bounded) and reads its nuspec. Returns
    /// null when the package is too large to introspect — the claim degrades to stub metadata.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is corrupt or has no nuspec.</exception>
    private static NuspecMetadata? ReadNestedNupkg(ZipArchiveEntry nupkgEntry)
    {
        if (nupkgEntry.Length > MaxNupkgBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (Stream entryStream = nupkgEntry.Open())
        {
            CopyBounded(entryStream, buffer, MaxNupkgBytes);
        }

        buffer.Position = 0;
        ZipArchive package;
        try
        {
            package = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "The Squirrel bootstrapper's release package is truncated or corrupt.", ex);
        }

        using (package)
        {
            ZipArchiveEntry? nuspec = null;
            int scanned = 0;
            foreach (ZipArchiveEntry entry in package.Entries)
            {
                if (++scanned > MaxEntriesScanned)
                {
                    break;
                }

                if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.Contains('/', StringComparison.Ordinal))
                {
                    nuspec = entry;
                    break;
                }
            }

            if (nuspec is null)
            {
                throw new InvalidDataException(
                    "The Squirrel bootstrapper's release package has no nuspec manifest.");
            }

            return ReadNuspec(nuspec);
        }
    }

    /// <summary>Reads a nuspec entry with a size bound.</summary>
    /// <exception cref="InvalidDataException">The manifest is oversized, truncated, or malformed.</exception>
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

    /// <summary>
    /// Builds the analysis. Squirrel installs per-user from <c>%LocalAppData%</c> without
    /// elevation regardless of what the payload contains, so scope, type and switches come
    /// from the bootstrapper; the nuspec contributes the identity Squirrel writes to ARP,
    /// and the release package's file name may promote the stub's architecture.
    /// </summary>
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

    /// <summary>
    /// True when the stub's version strings carry Squirrel bootstrap branding. The bare word
    /// "Squirrel" is deliberately not enough — only the bootstrap-specific tokens count.
    /// </summary>
    private static bool HasSquirrelMarker(VersionInfo version)
        => ContainsMarker(version.FileDescription)
            || ContainsMarker(version.ProductName)
            || ContainsMarker(version.CompanyName)
            || ContainsMarker(version.OriginalFilename);

    private static bool ContainsMarker(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (string token in _markerTokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> from a decompression stream whose declared
    /// size cannot be trusted.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream expands beyond <paramref name="maxBytes"/>.</exception>
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
}
