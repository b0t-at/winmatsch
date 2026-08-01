using System.IO.Compression;

namespace WinMatsch.Analysis;

internal enum InstallerContentKind
{
    Unknown,
    PortableExecutable,
    CompoundFile,
    Zip,
    Msix,
    MsixBundle,
}

/// <summary>Identifies outer packaging from magic bytes and required archive manifests.</summary>
internal static class InstallerContentDetector
{
    private static ReadOnlySpan<byte> CompoundFileMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static InstallerContentKind Detect(Stream stream, string fileName)
    {
        long originalPosition = stream.Position;
        try
        {
            Span<byte> prefix = stackalloc byte[8];
            stream.Position = 0;
            int read = stream.ReadAtLeast(prefix, 2, throwOnEndOfStream: false);
            ReadOnlySpan<byte> available = prefix[..read];
            if (available.Length >= 2 && available[0] == (byte)'M' && available[1] == (byte)'Z')
            {
                return InstallerContentKind.PortableExecutable;
            }

            if (available.Length >= CompoundFileMagic.Length && available[..8].SequenceEqual(CompoundFileMagic))
            {
                return InstallerContentKind.CompoundFile;
            }

            if (available.Length >= 4
                && available[0] == (byte)'P'
                && available[1] == (byte)'K'
                && ((available[2] == 3 && available[3] == 4)
                    || (available[2] == 5 && available[3] == 6)
                    || (available[2] == 7 && available[3] == 8)))
            {
                return DetectZipKind(stream, fileName);
            }

            return InstallerContentKind.Unknown;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static InstallerContentKind DetectZipKind(Stream stream, string fileName)
    {
        stream.Position = 0;
        try
        {
            using IDisposable scope = AnalysisLimits.EnterArchive($"'{fileName}'");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            AnalysisLimits.ValidateArchive(archive, $"'{fileName}'");
            bool hasPackageManifest = false;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (string.Equals(name, "AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase))
                {
                    return InstallerContentKind.MsixBundle;
                }

                if (string.Equals(name, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
                {
                    hasPackageManifest = true;
                }
            }

            return hasPackageManifest ? InstallerContentKind.Msix : InstallerContentKind.Zip;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new InvalidDataException($"'{fileName}' starts with ZIP magic but is not a readable archive.", exception);
        }
    }
}
