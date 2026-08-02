using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>Reads bounded PE architecture evidence from a 7-Zip self-extracting executable.</summary>
public sealed class SevenZipSfxProbe : IExeFormatProbe
{
    private const int MaxEntries = 256;
    private const long MaxPayloadBytes = 256L * 1024 * 1024;
    private const int MaxSignatureScanBytes = 1024 * 1024;
    private static readonly byte[] _sevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            long archiveOffset = FindArchiveOffset(stream);
            if (archiveOffset < 0)
            {
                return null;
            }

            using var archiveStream = new SubStream(stream, archiveOffset, stream.Length - archiveOffset);
            using var archive = SevenZipArchive.OpenArchive(
                archiveStream,
                new ReaderOptions { LeaveStreamOpen = true });
            var payloads = new List<(Architecture Architecture, long Size)>();
            int entries = 0;
            foreach (var entry in archive.Entries)
            {
                if (++entries > MaxEntries)
                {
                    throw new AnalysisResourceLimitException(
                        $"The 7-Zip self-extractor contains more than {MaxEntries} entries.");
                }

                if (entry.IsDirectory
                    || entry.Key is not { } name
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || IsAuxiliaryInstallerExecutable(name)
                    || entry.Size > MaxPayloadBytes)
                {
                    continue;
                }

                using Stream source = entry.OpenEntryStream();
                using var payload = new MemoryStream();
                source.CopyTo(payload);
                payload.Position = 0;
                PeImportInspection inspection = PeImportReader.Inspect(
                    payload,
                    PayloadDependencyAnalyzerOptions.DefaultMaximumImportDescriptors,
                    PayloadDependencyAnalyzerOptions.DefaultMaximumImportNameBytes);
                if (inspection.Architecture is { } architecture)
                {
                    payloads.Add((architecture, entry.Size));
                }
            }

            (Architecture? detectedArchitecture, AnalysisDiagnostic? diagnostic) = SelectArchitecture(payloads);
            IReadOnlyList<AnalysisDiagnostic> diagnostics = diagnostic is null ? [] : [diagnostic];
            return new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.GenericInstallerExe,
                Installers =
                [
                    new Installer
                    {
                        Architecture = detectedArchitecture,
                        InstallerType = InstallerType.Exe,
                        ElevationRequirement = peFile.RequestedElevation,
                    },
                ],
                ProductName = peFile.VersionInfo.ProductName,
                Publisher = peFile.VersionInfo.CompanyName,
                ProductVersion = peFile.VersionInfo.ProductVersion,
                Copyright = peFile.VersionInfo.LegalCopyright,
                IsSelfExtractorStub = true,
                Diagnostics = diagnostics,
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or SharpCompress.Common.ArchiveException)
        {
            throw new InvalidDataException("The 7-Zip self-extractor payload is truncated or corrupt.", exception);
        }
    }

    private static long FindArchiveOffset(Stream stream)
    {
        long overlayStart = PeOverlay.GetStart(stream);
        if (overlayStart <= 0)
        {
            return -1;
        }

        int length = (int)Math.Min(stream.Length - overlayStart, MaxSignatureScanBytes);
        byte[] bytes = new byte[length];
        stream.Position = overlayStart;
        stream.ReadExactly(bytes);
        int offset = bytes.AsSpan().IndexOf(_sevenZipSignature);
        return offset < 0 ? -1 : overlayStart + offset;
    }

    private static bool IsAuxiliaryInstallerExecutable(string path)
    {
        string normalized = path.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        return normalized.Contains("/uninstall/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "setup.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_installer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static (Architecture? Architecture, AnalysisDiagnostic? Diagnostic) SelectArchitecture(
        List<(Architecture Architecture, long Size)> payloads)
    {
        if (payloads.Count == 0)
        {
            return (null, new AnalysisDiagnostic(
                "SFX001",
                "No bounded embedded PE architecture evidence was available in the 7-Zip self-extractor.",
                RequiresManualAnalysis: true));
        }

        (Architecture Architecture, long Total, long Largest)[] weights =
        [
            .. payloads
                .GroupBy(static payload => payload.Architecture)
                .Select(static group => (
                    Architecture: group.Key,
                    Total: group.Sum(static payload => payload.Size),
                    Largest: group.Max(static payload => payload.Size)))
                .OrderByDescending(static weight => weight.Total)
                .ThenByDescending(static weight => weight.Largest),
        ];
        if (weights.Length == 1)
        {
            return (weights[0].Architecture, null);
        }

        if (weights[0].Total >= weights[1].Total * 2 && weights[0].Largest > weights[1].Largest)
        {
            return (weights[0].Architecture, new AnalysisDiagnostic(
                "SFX001",
                $"The 7-Zip self-extractor contains mixed PE architectures; {weights[0].Architecture} was selected from dominant payload size evidence.",
                RequiresManualAnalysis: true));
        }

        return (null, new AnalysisDiagnostic(
            "SFX001",
            $"The 7-Zip self-extractor contains mixed PE architectures ({string.Join(", ", weights.Select(static weight => weight.Architecture))}).",
            RequiresManualAnalysis: true));
    }
}
