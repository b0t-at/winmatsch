using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using WinMatsch.Analysis.Msi;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Advanced;

/// <summary>
/// Detects Advanced Installer setup executables: 7-Zip SFX containers whose PE overlay carries
/// a 7z archive wrapping the real MSI package. The probe locates the <c>7z¼¯'\x1C</c> signature
/// in the overlay, opens the archive, and analyzes the first embedded <c>.msi</c> entry with
/// <see cref="MsiAnalyzer"/> to harvest architecture, scope, product code, locale, and the Apps
/// &amp; Features evidence. The outer container always decides the classification: the analysis
/// is reported as <see cref="DetectedInstallerFormat.AdvancedInstaller"/> with
/// <see cref="InstallerType.Exe"/> and the SFX silent switches (<c>/exenoui /qn</c>), never as
/// the inner MSI — the MSI only contributes payload metadata the stub itself cannot carry.
/// Outer version-info strings win over inner MSI strings for the display metadata because the
/// vendor brands the wrapper, while the inner ARP row is kept verbatim as matching evidence.
/// </summary>
public sealed class AdvancedInstallerProbe : IExeFormatProbe
{
    /// <summary>7z archive signature: <c>'7' 'z' BC AF 27 1C</c>.</summary>
    private static readonly byte[] _sevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>Overlay window scanned for the 7z signature (SFX stubs put it at or near the overlay start).</summary>
    private const int MaxSignatureScanBytes = 1024 * 1024;

    /// <summary>Upper bound on the inner MSI copied to memory; larger payloads degrade to outer-only metadata.</summary>
    private const long MaxInnerMsiBytes = 256L * 1024 * 1024;

    /// <summary>Upper bound on archive entries inspected while looking for the MSI payload.</summary>
    private const int MaxEntriesScanned = 65536;

    /// <summary>
    /// Returns the installer's analysis, or null when the executable's overlay carries no
    /// 7z-SFX payload that identifies an Advanced Installer package.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is positively an Advanced Installer setup (version-info marker or an embedded
    /// <c>.msi</c> entry) but its 7z container or MSI payload is truncated or corrupt.
    /// </exception>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        long overlayStart = PeOverlay.GetStart(stream);
        if (overlayStart <= 0)
        {
            return null;
        }

        long archiveStart = PeOverlay.FindSignature(stream, overlayStart, _sevenZipSignature, MaxSignatureScanBytes);
        if (archiveStart < 0)
        {
            return null;
        }

        bool hasMarker = HasAdvancedInstallerMarker(peFile.VersionInfo);
        if (!TryFindMsiEntry(stream, archiveStart, out SevenZipArchive? archive, out SevenZipArchiveEntry? msiEntry))
        {
            // 7z signature present but the archive is unreadable: only the vendor marker makes
            // this positively an Advanced Installer; otherwise the overlay is just noise.
            return hasMarker
                ? throw new InvalidDataException(
                    "The file is an Advanced Installer setup, but its 7z payload archive is truncated or corrupt.")
                : null;
        }

        using (archive)
        {
            if (msiEntry is null && !hasMarker)
            {
                // A readable 7z SFX without an MSI payload is a generic self-extractor.
                return null;
            }

            InstallerAnalysis? inner = msiEntry is null ? null : AnalyzeInnerMsi(msiEntry);
            return Compose(peFile, inner);
        }
    }

    /// <summary>
    /// Opens the overlay archive and locates the first <c>.msi</c> entry. Returns false when
    /// the archive headers cannot be parsed; the archive is returned open (caller disposes)
    /// so the entry's payload stays readable.
    /// </summary>
    private static bool TryFindMsiEntry(
        Stream stream,
        long archiveStart,
        out SevenZipArchive? archive,
        out SevenZipArchiveEntry? msiEntry)
    {
        archive = null;
        msiEntry = null;
        try
        {
            var view = new SubStream(stream, archiveStart, stream.Length - archiveStart);
            archive = SevenZipArchive.Open(view, new ReaderOptions { LeaveStreamOpen = true });
            int scanned = 0;
            foreach (SevenZipArchiveEntry entry in archive.Entries)
            {
                if (++scanned > MaxEntriesScanned)
                {
                    break;
                }

                if (!entry.IsDirectory
                    && entry.Key is { } key
                    && key.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    msiEntry = entry;
                    break;
                }
            }

            return true;
        }
        catch (Exception ex) when (IsArchiveReadFailure(ex))
        {
            archive?.Dispose();
            archive = null;
            msiEntry = null;
            return false;
        }
    }

    /// <summary>
    /// Extracts the embedded MSI into memory (bounded) and analyzes it. Returns null when the
    /// entry is too large to introspect — the outer claim then degrades to stub metadata.
    /// </summary>
    /// <exception cref="InvalidDataException">The MSI payload is truncated or corrupt.</exception>
    private static InstallerAnalysis? AnalyzeInnerMsi(SevenZipArchiveEntry msiEntry)
    {
        if (msiEntry.Size > MaxInnerMsiBytes)
        {
            return null;
        }

        using var payload = new MemoryStream();
        try
        {
            using Stream entryStream = msiEntry.OpenEntryStream();
            CopyBounded(entryStream, payload, MaxInnerMsiBytes);
        }
        catch (Exception ex) when (IsArchiveReadFailure(ex))
        {
            throw new InvalidDataException(
                "The Advanced Installer setup's embedded MSI payload is truncated or corrupt.", ex);
        }

        payload.Position = 0;
        return new MsiAnalyzer().Analyze(payload, "embedded.msi");
    }

    /// <summary>
    /// Builds the analysis. The outer container decides format, installer type and switches;
    /// the inner MSI contributes payload facts the stub cannot know (architecture, scope,
    /// product code, locale, ARP row). Outer version strings win for display metadata.
    /// </summary>
    private static InstallerAnalysis Compose(PeFile peFile, InstallerAnalysis? inner)
    {
        Installer? innerInstaller = inner?.Installers[0];
        VersionInfo version = peFile.VersionInfo;

        var installer = new Installer
        {
            Architecture = innerInstaller?.Architecture ?? peFile.Architecture,
            InstallerType = InstallerType.Exe,
            Scope = innerInstaller?.Scope ?? peFile.ScopeHint,
            ElevationRequirement = peFile.RequestedElevation,
            InstallerLocale = innerInstaller?.InstallerLocale,
            ProductCode = innerInstaller?.ProductCode,
            InstallerSwitches = new InstallerSwitches
            {
                Silent = "/exenoui /qn",
                SilentWithProgress = "/exebasicui /qb",
            },
            AppsAndFeaturesEntries = innerInstaller?.AppsAndFeaturesEntries,
        };

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.AdvancedInstaller,
            Installers = [installer],
            ProductName = version.ProductName ?? inner?.ProductName,
            Publisher = version.CompanyName ?? inner?.Publisher,
            ProductVersion = version.ProductVersion ?? inner?.ProductVersion,
            Copyright = version.LegalCopyright ?? inner?.Copyright,
        };
    }

    /// <summary>
    /// True when the stub's version strings carry the Advanced Installer branding (the SFX
    /// stub ships with "Advanced Installer" / Caphyon vendor strings).
    /// </summary>
    private static bool HasAdvancedInstallerMarker(VersionInfo version)
        => ContainsMarker(version.FileDescription)
            || ContainsMarker(version.ProductName)
            || ContainsMarker(version.CompanyName)
            || ContainsMarker(version.OriginalFilename);

    private static bool ContainsMarker(string? value)
        => value is not null
            && (value.Contains("Advanced Installer", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Caphyon", StringComparison.OrdinalIgnoreCase));

    /// <summary>Exception shapes SharpCompress and stream plumbing surface on hostile or truncated archives.</summary>
    private static bool IsArchiveReadFailure(Exception ex)
        => ex is SharpCompressException
            or InvalidDataException
            or EndOfStreamException
            or ArgumentException
            or IndexOutOfRangeException
            or NotSupportedException
            or OverflowException
            or IOException
            // SharpCompress reports malformed 7z headers as ArchiveOperationException
            // (an InvalidOperationException) and can fault with NullReferenceException
            // on hostile header layouts; both mean "unreadable archive" here.
            or InvalidOperationException
            or NullReferenceException;

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
