using System.Buffers.Binary;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>Validates ZIP directory bounds before <see cref="System.IO.Compression.ZipArchive"/> allocates entries.</summary>
internal static class ZipArchiveBounds
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const int MaxCommentLength = ushort.MaxValue;
    private const int MaxEntryCount = 4096;
    private const long MaxCentralDirectoryBytes = 16L * 1024 * 1024;

    public static void Validate(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long savedPosition = stream.Position;
        try
        {
            if (!stream.CanSeek || stream.Length < 22)
            {
                throw Corrupt(description);
            }

            int tailLength = (int)Math.Min(stream.Length, 22L + MaxCommentLength);
            byte[] tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            stream.ReadExactly(tail);

            int eocdIndex = FindLast(tail, EndOfCentralDirectorySignature);
            if (eocdIndex < 0 || eocdIndex + 22 > tail.Length)
            {
                throw Corrupt(description);
            }

            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocdIndex + 20));
            if (eocdIndex + 22 + commentLength != tail.Length)
            {
                throw Corrupt(description);
            }

            ReadOnlySpan<byte> eocd = tail.AsSpan(eocdIndex);
            if (BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]) != 0)
            {
                throw new InvalidDataException($"{description} uses a multi-disk ZIP, which is not supported.");
            }

            ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
            ulong directorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
            ulong directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
            long eocdOffset = stream.Length - tailLength + eocdIndex;

            if (entryCount == ushort.MaxValue
                || directorySize == uint.MaxValue
                || directoryOffset == uint.MaxValue)
            {
                ReadZip64(stream, eocdOffset, description, out entryCount, out directorySize, out directoryOffset);
            }

            if (entryCount > MaxEntryCount)
            {
                throw new InvalidDataException($"{description} contains more than {MaxEntryCount} ZIP entries.");
            }

            if (directorySize > MaxCentralDirectoryBytes)
            {
                throw new InvalidDataException(
                    $"{description} has a ZIP central directory larger than {MaxCentralDirectoryBytes} bytes.");
            }

            if (directoryOffset > (ulong)eocdOffset
                || directorySize > (ulong)eocdOffset - directoryOffset)
            {
                throw Corrupt(description);
            }
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private static void ReadZip64(
        Stream stream,
        long eocdOffset,
        string description,
        out ulong entryCount,
        out ulong directorySize,
        out ulong directoryOffset)
    {
        entryCount = directorySize = directoryOffset = 0;
        if (eocdOffset < 20)
        {
            throw Corrupt(description);
        }

        Span<byte> locator = stackalloc byte[20];
        stream.Position = eocdOffset - locator.Length;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw Corrupt(description);
        }

        ulong recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (recordOffset > (ulong)eocdOffset || recordOffset + 56 > (ulong)eocdOffset)
        {
            throw Corrupt(description);
        }

        Span<byte> record = stackalloc byte[56];
        stream.Position = (long)recordOffset;
        stream.ReadExactly(record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature
            || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) != 0)
        {
            throw Corrupt(description);
        }

        entryCount = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        directorySize = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
        directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
    }

    private static int FindLast(ReadOnlySpan<byte> data, uint signature)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, signature);
        return data.LastIndexOf(bytes);
    }

    private static InvalidDataException Corrupt(string description)
        => new($"{description} is truncated or has an invalid ZIP directory.");
}
