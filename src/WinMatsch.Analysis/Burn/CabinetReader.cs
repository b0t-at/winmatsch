using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WinMatsch.Analysis.Burn;

/// <summary>
/// A minimal read-only Microsoft Cabinet (.cab) parser implemented from the documented
/// on-disk layout: a CFHEADER, then CFFOLDER entries, CFFILE entries at the header's file
/// offset, and per-folder chains of CFDATA blocks of at most 32 KiB uncompressed each.
/// Supports the uncompressed and MSZIP folder compression types — the ones WiX emits for
/// Burn containers. CFDATA checksums are not verified; multi-cabinet sets (files continued
/// from or into another cabinet) are rejected.
/// </summary>
internal static class CabinetReader
{
    private const uint HeaderSignature = 0x4643534D; // "MSCF" read little-endian.
    private const ushort ReservePresentFlag = 0x0004;
    private const ushort CompressionTypeMask = 0x000F;
    private const ushort CompressionNone = 0;
    private const ushort CompressionMsZip = 1;

    // iFolder values at or above 0xFFFD mark files continued from or into another cabinet.
    private const ushort ContinuedFolderThreshold = 0xFFFD;

    // MSZIP keeps the deflate history across the blocks of a folder, so a block may
    // back-reference up to a full 32 KiB window of previously decompressed folder data.
    private const int DeflateWindowSize = 32768;

    /// <summary>Reads the named file from the cabinet, or null when the cabinet has no such file.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a cabinet this reader understands.</exception>
    public static byte[]? ReadFile(byte[] cabinet, string fileName)
    {
        if (cabinet.Length < 36 || BinaryPrimitives.ReadUInt32LittleEndian(cabinet) != HeaderSignature)
        {
            throw new InvalidDataException("The data is not a cabinet: the MSCF signature is missing.");
        }

        int firstFileOffset = checked((int)ReadUInt32(cabinet, 16));
        int folderCount = ReadUInt16(cabinet, 26);
        int fileCount = ReadUInt16(cabinet, 28);
        int flags = ReadUInt16(cabinet, 30);
        if (fileCount > AnalysisLimits.MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"The cabinet contains {fileCount} files; the analysis limit is {AnalysisLimits.MaxArchiveEntries}.");
        }

        // CFHEADER optional reserve areas: per-header, per-folder and per-data sizes.
        int folderReserve = 0;
        int dataReserve = 0;
        int position = 36;
        if ((flags & ReservePresentFlag) != 0)
        {
            int headerReserve = ReadUInt16(cabinet, 36);
            folderReserve = ReadByte(cabinet, 38);
            dataReserve = ReadByte(cabinet, 39);
            position = 40 + headerReserve;
        }

        // CFFOLDER: first CFDATA offset (uint32), CFDATA count (uint16), compression (uint16).
        var folders = new (uint FirstDataOffset, int BlockCount, int Compression)[folderCount];
        for (int i = 0; i < folders.Length; i++)
        {
            folders[i] = (ReadUInt32(cabinet, position), ReadUInt16(cabinet, position + 4), ReadUInt16(cabinet, position + 6));
            position += 8 + folderReserve;
        }

        // CFFILE: uncompressed size (uint32), offset in the folder's uncompressed data
        // (uint32), folder index, date, time, attributes (uint16 each), null-terminated name.
        position = firstFileOffset;
        for (int i = 0; i < fileCount; i++)
        {
            uint fileLength = ReadUInt32(cabinet, position);
            uint folderOffset = ReadUInt32(cabinet, position + 4);
            int folderIndex = ReadUInt16(cabinet, position + 8);
            int nameStart = position + 16;
            int nameEnd = Array.IndexOf(cabinet, (byte)0, nameStart);
            if (nameEnd < 0)
            {
                throw new InvalidDataException("The cabinet is truncated inside a CFFILE name.");
            }

            if (Encoding.UTF8.GetString(cabinet, nameStart, nameEnd - nameStart) == fileName)
            {
                if (folderIndex >= ContinuedFolderThreshold)
                {
                    throw new InvalidDataException($"The cabinet file '{fileName}' spans multiple cabinets, which is not supported.");
                }

                if (folderIndex >= folders.Length)
                {
                    throw new InvalidDataException($"The cabinet file '{fileName}' references folder {folderIndex}, but the cabinet has only {folders.Length}.");
                }

                return ReadFromFolder(cabinet, folders[folderIndex], dataReserve, folderOffset, fileLength);
            }

            position = nameEnd + 1;
        }

        return null;
    }

    /// <summary>
    /// Decompresses a folder's CFDATA chain until the requested extent is available and
    /// returns the <paramref name="fileLength"/> bytes at <paramref name="folderOffset"/>.
    /// </summary>
    private static byte[] ReadFromFolder(
        byte[] cabinet,
        (uint FirstDataOffset, int BlockCount, int Compression) folder,
        int dataReserve,
        uint folderOffset,
        uint fileLength)
    {
        int compression = folder.Compression & CompressionTypeMask;
        if (compression is not CompressionNone and not CompressionMsZip)
        {
            throw new InvalidDataException(
                $"The cabinet uses unsupported compression type {compression}; only none and MSZIP are supported. Manual analysis is required.");
        }

        long needed = folderOffset + (long)fileLength;
        AnalysisLimits.ValidateAllocation(fileLength, "The cabinet file", AnalysisLimits.MaxEntryBytes);
        AnalysisLimits.ValidateAllocation(needed, "The expanded cabinet folder extent", AnalysisLimits.MaxExpandedArchiveBytes);
        using var output = new MemoryStream();
        int position = checked((int)folder.FirstDataOffset);
        for (int block = 0; block < folder.BlockCount && output.Length < needed; block++)
        {
            // CFDATA: checksum (uint32, not verified), compressed and uncompressed byte
            // counts (uint16 each), optional reserve, then the block data.
            int compressedLength = ReadUInt16(cabinet, position + 4);
            int uncompressedLength = ReadUInt16(cabinet, position + 6);
            int dataStart = position + 8 + dataReserve;
            if (dataStart + compressedLength > cabinet.Length)
            {
                throw new InvalidDataException("The cabinet is truncated inside a CFDATA block.");
            }

            ReadOnlySpan<byte> data = cabinet.AsSpan(dataStart, compressedLength);
            if (compression == CompressionNone)
            {
                if (compressedLength != uncompressedLength)
                {
                    throw new InvalidDataException("An uncompressed cabinet data block declares mismatching sizes.");
                }

                output.Write(data);
            }
            else
            {
                output.Write(InflateMsZipBlock(data, Dictionary(output), uncompressedLength));
            }

            position = dataStart + compressedLength;
        }

        if (output.Length < needed)
        {
            throw new InvalidDataException("The cabinet folder holds less data than its files declare.");
        }

        byte[] result = new byte[fileLength];
        output.Position = folderOffset;
        output.ReadExactly(result);
        return result;
    }

    /// <summary>The MSZIP history for the next block: the last window of folder data so far.</summary>
    private static ReadOnlySpan<byte> Dictionary(MemoryStream output)
    {
        int available = (int)Math.Min(output.Length, DeflateWindowSize);
        return output.GetBuffer().AsSpan((int)output.Length - available, available);
    }

    /// <summary>
    /// Inflates one MSZIP block: a "CK" signature followed by a raw deflate stream whose
    /// back-references may reach into previously decompressed folder data.
    /// <see cref="DeflateStream"/> cannot be primed with a dictionary, so a synthetic
    /// non-final "stored" deflate block carrying the history is prepended and its bytes are
    /// skipped from the inflated output.
    /// </summary>
    private static byte[] InflateMsZipBlock(ReadOnlySpan<byte> block, ReadOnlySpan<byte> dictionary, int uncompressedLength)
    {
        if (block.Length < 2 || block[0] != (byte)'C' || block[1] != (byte)'K')
        {
            throw new InvalidDataException("An MSZIP cabinet data block does not start with the CK signature.");
        }

        byte[] input = new byte[5 + dictionary.Length + block.Length - 2];
        input[0] = 0x00; // BFINAL = 0, BTYPE = 00 (stored), padded to the byte boundary.
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(1), (ushort)dictionary.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(3), (ushort)~dictionary.Length);
        dictionary.CopyTo(input.AsSpan(5));
        block[2..].CopyTo(input.AsSpan(5 + dictionary.Length));

        using var deflate = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress);
        byte[] inflated = new byte[dictionary.Length + uncompressedLength];
        try
        {
            deflate.ReadExactly(inflated);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("An MSZIP cabinet data block inflated to fewer bytes than it declares.", exception);
        }

        return inflated[dictionary.Length..];
    }

    private static byte ReadByte(byte[] cabinet, int offset)
    {
        if (offset >= cabinet.Length)
        {
            throw new InvalidDataException("The cabinet is truncated.");
        }

        return cabinet[offset];
    }

    private static ushort ReadUInt16(byte[] cabinet, int offset)
    {
        if (offset < 0 || offset + 2 > cabinet.Length)
        {
            throw new InvalidDataException("The cabinet is truncated.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(offset));
    }

    private static uint ReadUInt32(byte[] cabinet, int offset)
    {
        if (offset < 0 || offset + 4 > cabinet.Length)
        {
            throw new InvalidDataException("The cabinet is truncated.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(cabinet.AsSpan(offset));
    }
}
