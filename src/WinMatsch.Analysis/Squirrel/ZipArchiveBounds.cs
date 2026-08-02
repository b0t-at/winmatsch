using System.Buffers.Binary;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>Validates ZIP directory bounds before <see cref="System.IO.Compression.ZipArchive"/> allocates entries.</summary>
internal static class ZipArchiveBounds
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const int CentralDirectoryHeaderSize = 46;
    private const int MaxCommentLength = ushort.MaxValue;
    private const int DefaultMaxEntryCount = AnalysisLimits.MaxArchiveEntries;
    private const long DefaultMaxCentralDirectoryBytes = AnalysisLimits.MaxDependencyCentralDirectoryBytes;

    public static void Validate(Stream stream, string description)
        => Validate(
            stream,
            description,
            DefaultMaxEntryCount,
            DefaultMaxCentralDirectoryBytes);

    public static void Validate(
        Stream stream,
        string description,
        int maximumEntryCount,
        long maximumCentralDirectoryBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCentralDirectoryBytes);
        if (!stream.CanSeek)
        {
            throw new InvalidDataException($"{description} must be seekable for bounded ZIP validation.");
        }

        long savedPosition = stream.Position;
        try
        {
            if (stream.Length < 22)
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

            ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
            ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
            if (entriesOnDisk != totalEntries)
            {
                throw Corrupt(description);
            }

            ulong entryCount = totalEntries;
            ulong directorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
            ulong directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
            long eocdOffset = stream.Length - tailLength + eocdIndex;
            ulong directoryEnd = (ulong)eocdOffset;

            if (entryCount == ushort.MaxValue
                || directorySize == uint.MaxValue
                || directoryOffset == uint.MaxValue)
            {
                ReadZip64(
                    stream,
                    eocdOffset,
                    description,
                    out entryCount,
                    out directorySize,
                    out directoryOffset,
                    out directoryEnd);
            }

            if (directoryOffset > directoryEnd
                || directorySize > directoryEnd - directoryOffset
                || entryCount > directorySize / CentralDirectoryHeaderSize)
            {
                throw Corrupt(description);
            }

            if (entryCount > (ulong)maximumEntryCount)
            {
                throw new AnalysisResourceLimitException(
                    $"{description} contains more than {maximumEntryCount} ZIP entries.");
            }

            if (directorySize > (ulong)maximumCentralDirectoryBytes)
            {
                throw new AnalysisResourceLimitException(
                    $"{description} has a ZIP central directory larger than {maximumCentralDirectoryBytes} bytes.");
            }

            ValidateCentralDirectory(
                stream,
                description,
                directoryOffset,
                directorySize,
                directoryEnd,
                entryCount,
                maximumCentralDirectoryBytes);
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
        out ulong directoryOffset,
        out ulong directoryEnd)
    {
        entryCount = directorySize = directoryOffset = directoryEnd = 0;
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

        ulong entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
        entryCount = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        if (entriesOnDisk != entryCount)
        {
            throw Corrupt(description);
        }

        directorySize = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
        directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
        directoryEnd = recordOffset;
    }

    private static void ValidateCentralDirectory(
        Stream stream,
        string description,
        ulong directoryOffset,
        ulong declaredSize,
        ulong directoryEnd,
        ulong entryCount,
        long maximumBytes)
    {
        const uint CentralDirectoryHeaderSignature = 0x02014B50;
        if (directoryOffset > directoryEnd
            || directoryEnd > (ulong)stream.Length
            || directoryOffset > long.MaxValue)
        {
            throw Corrupt(description);
        }

        ulong position = directoryOffset;
        Span<byte> header = stackalloc byte[CentralDirectoryHeaderSize];
        Span<byte> optionalRecordHeader = stackalloc byte[8];
        for (ulong i = 0; i < entryCount; i++)
        {
            if (position > directoryEnd || directoryEnd - position < CentralDirectoryHeaderSize)
            {
                throw Corrupt(description);
            }

            stream.Position = (long)position;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryHeaderSignature)
            {
                throw Corrupt(description);
            }

            ulong variableSize = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            ulong recordSize = CentralDirectoryHeaderSize + variableSize;
            if (recordSize > directoryEnd - position)
            {
                throw Corrupt(description);
            }

            if (position - directoryOffset + recordSize > (ulong)maximumBytes)
            {
                throw new AnalysisResourceLimitException(
                    $"{description} has an actual ZIP central directory larger than {maximumBytes} bytes.");
            }

            position += recordSize;
        }

        if (position < directoryEnd)
        {
            if (directoryEnd - position < 6)
            {
                throw Corrupt(description);
            }

            stream.Position = (long)position;
            stream.ReadExactly(optionalRecordHeader[..4]);
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(optionalRecordHeader);
            if (signature != 0x05054B50)
            {
                throw Corrupt(description);
            }

            stream.ReadExactly(optionalRecordHeader.Slice(4, 2));
            ulong recordSize = 6UL + BinaryPrimitives.ReadUInt16LittleEndian(optionalRecordHeader[4..]);
            if (recordSize > directoryEnd - position)
            {
                throw Corrupt(description);
            }

            if (position - directoryOffset + recordSize > (ulong)maximumBytes)
            {
                throw new AnalysisResourceLimitException(
                    $"{description} has an optional ZIP central-directory record outside the configured bounds.");
            }

            position += recordSize;
        }

        ulong actualSize = position - directoryOffset;
        if (position != directoryEnd || actualSize != declaredSize)
        {
            throw new InvalidDataException(
                $"{description} has inconsistent declared and actual ZIP central-directory bounds.");
        }
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
