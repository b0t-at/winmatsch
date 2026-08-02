using System.IO.Compression;
using WinMatsch.Analysis.Advanced;
using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>Detects executable Java archives and derives architecture from bundled native libraries.</summary>
public sealed class JavaArchiveProbe : IExeFormatProbe
{
    private const int MaxSignatureScanBytes = 16 * 1024 * 1024;
    private static readonly byte[] _zipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] _zipEndSignature = [0x50, 0x4B, 0x05, 0x06];

    public InstallerAnalysis? Probe(Pe.PeFile peFile, Stream stream)
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
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            bool hasManifest = archive.GetEntry("META-INF/MANIFEST.MF") is not null;
            bool hasClasses = archive.Entries.Any(static entry => entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
            if (!hasManifest || !hasClasses)
            {
                return null;
            }

            var architectures = new HashSet<Architecture>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Architecture? entryArchitecture = UrlArchitectureDetector.Detect(entry.FullName);
                if (entryArchitecture is { } detected)
                {
                    architectures.Add(detected);
                }
            }

            Architecture architecture = architectures.Count == 1 ? architectures.Single() : Architecture.Neutral;
            return new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.PortableExe,
                Installers = [new Installer { Architecture = architecture, InstallerType = InstallerType.Portable }],
                ProductName = peFile.VersionInfo.ProductName,
                Publisher = peFile.VersionInfo.CompanyName,
                ProductVersion = peFile.VersionInfo.ProductVersion,
                Copyright = peFile.VersionInfo.LegalCopyright,
                Diagnostics =
                [
                    new AnalysisDiagnostic(
                        "JAVA001",
                        architectures.Count > 1
                            ? $"The Java archive bundles native libraries for {string.Join(", ", architectures)}; the portable application was reported as neutral."
                            : "The executable is a Java archive; architecture was derived from its bundled native libraries.",
                        RequiresManualAnalysis: false),
                ],
            };
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static long FindArchiveOffset(Stream stream)
    {
        int tailLength = (int)Math.Min(stream.Length, 65557);
        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        if (tail.AsSpan().LastIndexOf(_zipEndSignature) < 0)
        {
            return -1;
        }

        long overlayStart = PeOverlay.GetStart(stream);
        if (overlayStart <= 0)
        {
            return -1;
        }

        int length = (int)Math.Min(stream.Length - overlayStart, MaxSignatureScanBytes);
        byte[] bytes = new byte[length];
        stream.Position = overlayStart;
        stream.ReadExactly(bytes);
        int offset = bytes.AsSpan().IndexOf(_zipSignature);
        return offset < 0 ? -1 : overlayStart + offset;
    }
}
