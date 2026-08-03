using System.Buffers.Binary;

namespace WinMatsch.Analysis.Nsis;

/// <summary>
/// The NSIS "first header" — the 28-byte record that starts the installer archive in the PE
/// overlay (NSIS <c>Source/exehead/fileform.h</c>, <c>firstheader</c>): flags, the signature
/// <c>0xDEADBEEF</c> followed by the ASCII magic "NullsoftInst", the byte length of the
/// compressed installer header, and the length of all following data. The NSIS stub locates
/// it by scanning the file in 512-byte steps; this parser scans only the overlay (the data
/// after the last PE section's raw data), which is where makensis places it.
/// </summary>
internal sealed class NsisFirstHeader
{
    /// <summary>The size of the on-disk first header record.</summary>
    public const int Size = 28;

    // firstheader.flags bits (FH_FLAGS_*): 1 = uninstaller, 2 = silent, 4 = no CRC, 8 = force CRC.
    private const uint Signature = 0xDEADBEEF;
    private const int ScanStep = 512;
    private const int MaxSignatureScanBytes = 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "NullsoftInst"u8;

    private NsisFirstHeader(uint flags, int headerSize, uint followingDataSize, long dataOffset)
    {
        Flags = flags;
        HeaderSize = headerSize;
        FollowingDataSize = followingDataSize;
        DataOffset = dataOffset;
    }

    /// <summary>The FH_FLAGS_* bits; bit 1 marks the record as an uninstaller's.</summary>
    public uint Flags { get; }

    /// <summary>The size in bytes of the installer header once decompressed.</summary>
    public int HeaderSize { get; }

    /// <summary>The declared length of all data following the first header.</summary>
    public uint FollowingDataSize { get; }

    /// <summary>The stream offset of the first byte after the first header (the data blocks).</summary>
    public long DataOffset { get; }

    /// <summary>
    /// Scans the first MiB of the PE overlay in 512-byte steps for the NSIS signature and
    /// parses the first header. Makensis places the record at the first aligned position;
    /// the bounded allowance tolerates nonstandard padding without scanning arbitrary payloads.
    /// Returns null when the file has no overlay or no signature — it is not an NSIS installer.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The signature was found but the declared header size is implausible.
    /// </exception>
    public static NsisFirstHeader? Find(Stream stream)
    {
        long overlayStart = GetOverlayStart(stream);
        if (overlayStart <= 0)
        {
            return null;
        }

        // makensis pads the stub to the 512-byte scan granularity before appending the archive.
        long position = (overlayStart + ScanStep - 1) / ScanStep * ScanStep;
        Span<byte> record = stackalloc byte[Size];
        long scanEnd = Math.Min(stream.Length, position + MaxSignatureScanBytes);
        for (; position + Size <= scanEnd; position += ScanStep)
        {
            stream.Position = position;
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != Signature
                || !record[8..20].SequenceEqual(Magic))
            {
                continue;
            }

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(record);
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
            uint followingDataSize = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            if (headerSize is <= 0 or > AnalysisLimits.MaxNsisHeaderBytes)
            {
                throw new InvalidDataException(
                    $"The NSIS first header declares an implausible installer header size of {headerSize} bytes.");
            }

            return new NsisFirstHeader(flags, headerSize, followingDataSize, position + Size);
        }

        return null;
    }

    /// <summary>
    /// Walks the PE section table on the raw stream (DOS header → COFF header → section
    /// headers) and returns the end of the last section's raw data — the start of the
    /// overlay. Structural shortfalls yield 0: the PE was already validated by
    /// <see cref="Pe.PeFile"/>, so anything unreadable here simply has no NSIS archive.
    /// </summary>
    private static long GetOverlayStart(Stream stream)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        if (!TryReadAt(stream, 0, dosHeader) || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return 0;
        }

        // COFF header after the "PE\0\0" signature: NumberOfSections at offset 2,
        // SizeOfOptionalHeader at offset 16; the section table follows the optional header.
        uint peHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader[60..]);
        Span<byte> coffHeader = stackalloc byte[24];
        if (!TryReadAt(stream, peHeaderOffset, coffHeader)
            || BinaryPrimitives.ReadUInt32LittleEndian(coffHeader) != 0x00004550)
        {
            return 0;
        }

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[6..]);
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[20..]);
        if (sectionCount is <= 0 or > AnalysisLimits.MaxPeSections)
        {
            return 0;
        }

        byte[] table = new byte[sectionCount * 40];
        if (!TryReadAt(stream, peHeaderOffset + 24 + (uint)optionalHeaderSize, table))
        {
            return 0;
        }

        long overlayStart = 0;
        for (int i = 0; i < sectionCount; i++)
        {
            // IMAGE_SECTION_HEADER: SizeOfRawData at offset 16, PointerToRawData at offset 20.
            ReadOnlySpan<byte> entry = table.AsSpan(i * 40, 40);
            long sectionEnd = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..])
                + (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            overlayStart = Math.Max(overlayStart, sectionEnd);
        }

        return overlayStart;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }
}
