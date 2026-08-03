using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Hand-writes minimal but spec-correct 7z archives for SFX probe tests: Copy codec, one
/// folder per file, names stored inline. The writer is produced independently from the
/// documented 7z format (signature header with CRCs, kPackInfo/kUnpackInfo/kFilesInfo
/// property blocks, variable-length number encoding), so the fixtures double as a
/// cross-check of the production reader. SharpCompress can read 7z but not write it, which
/// is why this writer exists.
/// </summary>
internal static class SevenZipFixtures
{
    private static readonly uint[] _crcTable = BuildCrcTable();

    /// <summary>Builds a 7z archive containing the given entries (stored, one folder each).</summary>
    public static byte[] Build(params (string Name, byte[] Data)[] entries)
        => Build(entries, firstEntryDeclaredSize: null);

    /// <summary>
    /// Builds a 7z archive; when <paramref name="firstEntryDeclaredSize"/> is set, the first
    /// entry's unpacked size is declared as that value regardless of its actual data — used
    /// to simulate hostile archives announcing implausibly large payloads.
    /// </summary>
    public static byte[] Build((string Name, byte[] Data)[] entries, long? firstEntryDeclaredSize)
    {
        var packed = new MemoryStream();
        foreach ((_, byte[] data) in entries)
        {
            packed.Write(data);
        }

        byte[] header = BuildHeader(entries, firstEntryDeclaredSize);

        var startHeader = new MemoryStream();
        WriteUInt64(startHeader, (ulong)packed.Length);
        WriteUInt64(startHeader, (ulong)header.Length);
        WriteUInt32(startHeader, Crc32(header));
        byte[] startHeaderBytes = startHeader.ToArray();

        var output = new MemoryStream();
        output.Write([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        WriteUInt32(output, Crc32(startHeaderBytes));
        output.Write(startHeaderBytes);
        packed.WriteTo(output);
        output.Write(header);
        return output.ToArray();
    }

    /// <summary>Standard CRC-32 (reflected, polynomial 0xEDB88320) as used by 7z and zip.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ _crcTable[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static byte[] BuildHeader((string Name, byte[] Data)[] entries, long? firstEntryDeclaredSize)
    {
        int count = entries.Length;
        var header = new MemoryStream();
        header.WriteByte(0x01); // kHeader

        header.WriteByte(0x04); // kMainStreamsInfo
        header.WriteByte(0x06); // kPackInfo
        WriteNumber(header, 0); // PackPos
        WriteNumber(header, (ulong)count);
        header.WriteByte(0x09); // kSize
        foreach ((_, byte[] data) in entries)
        {
            WriteNumber(header, (ulong)data.Length);
        }

        header.WriteByte(0x00); // kEnd (PackInfo)

        header.WriteByte(0x07); // kUnpackInfo
        header.WriteByte(0x0B); // kFolder
        WriteNumber(header, (ulong)count);
        header.WriteByte(0x00); // external = 0
        for (int i = 0; i < count; i++)
        {
            WriteNumber(header, 1); // one coder
            header.WriteByte(0x01); // coder flags: 1-byte codec id, simple
            header.WriteByte(0x00); // codec id: Copy
        }

        header.WriteByte(0x0C); // kCodersUnpackSize
        for (int i = 0; i < count; i++)
        {
            ulong size = i == 0 && firstEntryDeclaredSize is { } declared
                ? (ulong)declared
                : (ulong)entries[i].Data.Length;
            WriteNumber(header, size);
        }

        header.WriteByte(0x00); // kEnd (UnpackInfo)
        header.WriteByte(0x08); // kSubStreamsInfo (empty: one substream per folder)
        header.WriteByte(0x00); // kEnd (SubStreamsInfo)
        header.WriteByte(0x00); // kEnd (StreamsInfo)

        header.WriteByte(0x05); // kFilesInfo
        WriteNumber(header, (ulong)count);
        header.WriteByte(0x11); // kName
        var names = new MemoryStream();
        names.WriteByte(0x00); // external = 0
        foreach ((string name, _) in entries)
        {
            names.Write(Encoding.Unicode.GetBytes(name));
            names.WriteByte(0x00);
            names.WriteByte(0x00);
        }

        WriteNumber(header, (ulong)names.Length);
        names.WriteTo(header);
        header.WriteByte(0x00); // kEnd (FilesInfo)

        header.WriteByte(0x00); // kEnd (Header)
        return header.ToArray();
    }

    /// <summary>7z variable-length number encoding (leading byte carries length flags and high bits).</summary>
    private static void WriteNumber(Stream stream, ulong value)
    {
        byte firstByte = 0;
        byte mask = 0x80;
        int extraBytes;
        for (extraBytes = 0; extraBytes < 8; extraBytes++)
        {
            if (value < 1UL << (7 * (extraBytes + 1)))
            {
                firstByte |= (byte)(value >> (8 * extraBytes));
                break;
            }

            firstByte |= mask;
            mask >>= 1;
        }

        stream.WriteByte(firstByte);
        ulong remaining = value;
        for (int i = 0; i < extraBytes; i++)
        {
            stream.WriteByte((byte)remaining);
            remaining >>= 8;
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
