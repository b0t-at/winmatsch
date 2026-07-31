using System.Buffers.Binary;
using System.Text;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using WinMatsch.Analysis.Msi;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Advanced;

/// <summary>
/// Detects Advanced Installer executables from their <c>ADVINSTSFX</c> footer and file
/// table. Direct MSI records and MSI files in table-declared 7z records are inspected,
/// while the outer EXE retains format and switch precedence.
/// </summary>
public sealed class AdvancedInstallerProbe : IExeFormatProbe
{
    private static readonly byte[] _footerSignature = "ADVINSTSFX"u8.ToArray();
    private const int FooterSize = 74;
    private const int SignatureOffset = 64;
    private const int FooterSearchBytes = 16 * 1024;
    private const int FileEntrySize = 24;
    private const int MaxFileEntries = 4096;
    private const int MaxEntryNameCharacters = 4096;
    private const long MaxPayloadBytes = 256L * 1024 * 1024;
    private const int XorPrefixBytes = 0x200;

    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        Footer? footer = FindFooter(stream);
        if (footer is null)
        {
            return null;
        }

        List<FileEntry> entries = ReadFileTable(stream, footer.Value);
        List<InnerMsi> payloads = [];
        foreach (FileEntry entry in entries)
        {
            if (entry.IsMsi)
            {
                byte[]? data = ReadFile(stream, entry);
                if (data is not null)
                {
                    payloads.Add(AnalyzeMsi(data, entry.Name));
                }
            }
            else if (entry.IsSevenZip)
            {
                byte[]? data = ReadFile(stream, entry);
                if (data is not null)
                {
                    payloads.AddRange(ReadSevenZipMsis(data));
                }
            }
        }

        return Compose(peFile, payloads);
    }

    private static Footer? FindFooter(Stream stream)
    {
        if (stream.Length < FooterSize)
        {
            return null;
        }

        int windowLength = (int)Math.Min(stream.Length, FooterSearchBytes);
        byte[] window = new byte[windowLength];
        long windowOffset = stream.Length - windowLength;
        stream.Position = windowOffset;
        stream.ReadExactly(window);
        int signatureIndex = window.AsSpan().LastIndexOf(_footerSignature);
        if (signatureIndex < SignatureOffset)
        {
            return null;
        }

        long footerOffset = windowOffset + signatureIndex - SignatureOffset;
        Span<byte> bytes = stackalloc byte[FooterSize];
        stream.Position = footerOffset;
        stream.ReadExactly(bytes);
        uint recordedOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (recordedOffset != footerOffset || version != 100)
        {
            return null;
        }

        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint infoOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        uint tablePointer = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        uint fileDataStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        if (fileCount > MaxFileEntries
            || tablePointer >= footerOffset
            || fileDataStart > tablePointer
            || infoOffset > stream.Length)
        {
            throw new InvalidDataException(
                "The Advanced Installer ADVINSTSFX footer contains invalid table offsets or counts.");
        }

        return new Footer(footerOffset, fileCount, tablePointer);
    }

    private static List<FileEntry> ReadFileTable(Stream stream, Footer footer)
    {
        var entries = new List<FileEntry>((int)footer.FileCount);
        stream.Position = footer.TablePointer;
        byte[] raw = new byte[FileEntrySize];
        for (uint i = 0; i < footer.FileCount; i++)
        {
            if (stream.Position > footer.Offset - FileEntrySize)
            {
                throw new InvalidDataException("The Advanced Installer file table is truncated.");
            }

            stream.ReadExactly(raw);
            uint type0 = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            uint type1 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
            uint xorFlag = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
            uint nameCharacters = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
            if (nameCharacters > MaxEntryNameCharacters
                || nameCharacters > (footer.Offset - stream.Position) / 2)
            {
                throw new InvalidDataException("The Advanced Installer file table contains an invalid entry name.");
            }

            byte[] nameBytes = new byte[checked((int)nameCharacters * 2)];
            stream.ReadExactly(nameBytes);
            string name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
            if (offset > stream.Length || size > stream.Length - offset)
            {
                throw new InvalidDataException(
                    $"The Advanced Installer file table entry '{name}' points outside the executable.");
            }

            entries.Add(new FileEntry(type0, type1, xorFlag, offset, size, name));
        }

        return entries;
    }

    private static byte[]? ReadFile(Stream stream, FileEntry entry)
    {
        if (entry.Size > MaxPayloadBytes)
        {
            return null;
        }

        byte[] data = new byte[entry.Size];
        stream.Position = entry.Offset;
        stream.ReadExactly(data);
        if (entry.XorFlag == 2)
        {
            for (int i = 0; i < Math.Min(data.Length, XorPrefixBytes); i++)
            {
                data[i] ^= 0xFF;
            }
        }

        return data;
    }

    private static InnerMsi AnalyzeMsi(byte[] data, string name)
    {
        try
        {
            using var payload = new MemoryStream(data, writable: false);
            InstallerAnalysis analysis = new MsiAnalyzer().Analyze(payload, name);
            payload.Position = 0;
            bool hidden = AdvancedMsiProperties.IsArpSystemComponent(payload);
            return new InnerMsi(analysis, hidden);
        }
        catch (Exception ex) when (IsPayloadReadFailure(ex))
        {
            throw new InvalidDataException(
                $"The Advanced Installer embedded MSI '{name}' is truncated or corrupt.", ex);
        }
    }

    private static List<InnerMsi> ReadSevenZipMsis(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using SevenZipArchive archive = SevenZipArchive.Open(
                stream,
                new ReaderOptions { LeaveStreamOpen = true });
            List<InnerMsi> payloads = [];
            int scanned = 0;
            foreach (SevenZipArchiveEntry entry in archive.Entries)
            {
                if (++scanned > MaxFileEntries)
                {
                    throw new InvalidDataException("The Advanced Installer nested 7z contains too many entries.");
                }

                if (entry.IsDirectory
                    || entry.Key is not { } name
                    || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                    || entry.Size > MaxPayloadBytes)
                {
                    continue;
                }

                using Stream source = entry.OpenEntryStream();
                using var payload = new MemoryStream();
                CopyBounded(source, payload, MaxPayloadBytes);
                payloads.Add(AnalyzeMsi(payload.ToArray(), name));
            }

            return payloads;
        }
        catch (Exception ex) when (IsArchiveReadFailure(ex))
        {
            throw new InvalidDataException(
                "The Advanced Installer nested 7z payload is truncated or corrupt.", ex);
        }
    }

    private static InstallerAnalysis Compose(PeFile peFile, List<InnerMsi> payloads)
    {
        VersionInfo version = peFile.VersionInfo;
        List<Installer> installers = payloads.Count == 0
            ? [CreateInstaller(peFile, inner: null)]
            : payloads.Select(payload => CreateInstaller(peFile, payload)).ToList();
        InstallerAnalysis? visible = payloads.FirstOrDefault(static payload => !payload.Hidden)?.Analysis;

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.AdvancedInstaller,
            Installers = installers,
            ProductName = version.ProductName ?? visible?.ProductName,
            Publisher = version.CompanyName ?? visible?.Publisher,
            ProductVersion = version.ProductVersion ?? visible?.ProductVersion,
            Copyright = version.LegalCopyright ?? visible?.Copyright,
        };
    }

    private static Installer CreateInstaller(PeFile peFile, InnerMsi? inner)
    {
        Installer? source = inner?.Analysis.Installers[0];
        bool hidden = inner?.Hidden == true;
        return new Installer
        {
            Architecture = source?.Architecture ?? peFile.Architecture,
            InstallerType = InstallerType.Exe,
            Scope = source?.Scope ?? peFile.ScopeHint,
            ElevationRequirement = peFile.RequestedElevation,
            InstallerLocale = source?.InstallerLocale,
            ProductCode = hidden ? null : source?.ProductCode,
            InstallerSwitches = new InstallerSwitches
            {
                Silent = "/exenoui /qn",
                SilentWithProgress = "/exebasicui /qb",
            },
            AppsAndFeaturesEntries = hidden ? null : source?.AppsAndFeaturesEntries,
        };
    }

    private static bool IsPayloadReadFailure(Exception ex)
        => ex is InvalidDataException
            or EndOfStreamException
            or ArgumentException
            or IndexOutOfRangeException
            or NotSupportedException
            or OverflowException
            or IOException;

    private static bool IsArchiveReadFailure(Exception ex)
        => ex is SharpCompressException
            or InvalidDataException
            or EndOfStreamException
            or ArgumentException
            or IndexOutOfRangeException
            or NotSupportedException
            or OverflowException
            or IOException
            or InvalidOperationException
            or NullReferenceException;

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

    private readonly record struct Footer(long Offset, uint FileCount, uint TablePointer);

    private sealed record FileEntry(uint Type0, uint Type1, uint XorFlag, uint Offset, uint Size, string Name)
    {
        public bool IsMsi => Type0 == 1 && Type1 == 0;

        public bool IsSevenZip => Type0 == 3 && Type1 == 7;
    }

    private sealed record InnerMsi(InstallerAnalysis Analysis, bool Hidden);
}
