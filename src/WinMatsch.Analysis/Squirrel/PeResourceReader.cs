using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Squirrel;

/// <summary>Reads one named PE resource without relying on Win32 resource APIs.</summary>
internal static class PeResourceReader
{
    private const uint SubdirectoryFlag = 0x80000000;

    public static byte[]? Read(Stream stream, string typeName, int resourceId)
    {
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            PEHeader? header = reader.PEHeaders.PEHeader;
            if (header is null)
            {
                return null;
            }

            DirectoryEntry directory = header.ResourceTableDirectory;
            if (directory.RelativeVirtualAddress == 0 || directory.Size <= 0)
            {
                return null;
            }

            ReadOnlySpan<byte> resources = reader.GetSectionData(directory.RelativeVirtualAddress).GetContent().AsSpan();
            (int Offset, bool Directory)? type = FindNamedEntry(resources, 0, typeName);
            if (type is not { Directory: true })
            {
                return null;
            }

            (int Offset, bool Directory)? name = FindIdEntry(resources, type.Value.Offset, resourceId);
            if (name is not { Directory: true })
            {
                return null;
            }

            (int Offset, bool Directory)? language = FirstEntry(resources, name.Value.Offset);
            if (language is not { Directory: false })
            {
                return null;
            }

            int dataEntryOffset = language.Value.Offset;
            if (!Contains(resources, dataEntryOffset, 16))
            {
                return null;
            }

            uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(resources[dataEntryOffset..]);
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(resources[(dataEntryOffset + 4)..]);
            long dataOffset = dataRva - (long)(uint)directory.RelativeVirtualAddress;
            return dataSize > 0
                && dataSize <= int.MaxValue
                && dataOffset >= 0
                && dataOffset + dataSize <= resources.Length
                    ? resources.Slice((int)dataOffset, (int)dataSize).ToArray()
                    : null;
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static (int Offset, bool Directory)? FindNamedEntry(
        ReadOnlySpan<byte> resources,
        int directoryOffset,
        string name)
    {
        if (!TryGetCounts(resources, directoryOffset, out int namedCount, out _))
        {
            return null;
        }

        for (int i = 0; i < namedCount; i++)
        {
            int entryOffset = directoryOffset + 16 + (i * 8);
            if (!Contains(resources, entryOffset, 8))
            {
                return null;
            }

            uint rawName = BinaryPrimitives.ReadUInt32LittleEndian(resources[entryOffset..]);
            if ((rawName & SubdirectoryFlag) == 0)
            {
                continue;
            }

            int nameOffset = (int)(rawName & ~SubdirectoryFlag);
            if (!Contains(resources, nameOffset, 2))
            {
                return null;
            }

            int characterCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[nameOffset..]);
            int byteCount = checked(characterCount * 2);
            if (Contains(resources, nameOffset + 2, byteCount)
                && Encoding.Unicode.GetString(resources.Slice(nameOffset + 2, byteCount))
                    .Equals(name, StringComparison.Ordinal))
            {
                return ReadTarget(resources, entryOffset);
            }
        }

        return null;
    }

    private static (int Offset, bool Directory)? FindIdEntry(
        ReadOnlySpan<byte> resources,
        int directoryOffset,
        int id)
    {
        if (!TryGetCounts(resources, directoryOffset, out int namedCount, out int idCount))
        {
            return null;
        }

        for (int i = namedCount; i < namedCount + idCount; i++)
        {
            int entryOffset = directoryOffset + 16 + (i * 8);
            if (!Contains(resources, entryOffset, 8))
            {
                return null;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(resources[entryOffset..]) == (uint)id)
            {
                return ReadTarget(resources, entryOffset);
            }
        }

        return null;
    }

    private static (int Offset, bool Directory)? FirstEntry(ReadOnlySpan<byte> resources, int directoryOffset)
    {
        if (!TryGetCounts(resources, directoryOffset, out int namedCount, out int idCount)
            || namedCount + idCount == 0)
        {
            return null;
        }

        return ReadTarget(resources, directoryOffset + 16);
    }

    private static bool TryGetCounts(
        ReadOnlySpan<byte> resources,
        int directoryOffset,
        out int namedCount,
        out int idCount)
    {
        namedCount = 0;
        idCount = 0;
        if (!Contains(resources, directoryOffset, 16))
        {
            return false;
        }

        namedCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 12)..]);
        idCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 14)..]);
        return namedCount + idCount <= (resources.Length - directoryOffset - 16) / 8;
    }

    private static (int Offset, bool Directory)? ReadTarget(ReadOnlySpan<byte> resources, int entryOffset)
    {
        if (!Contains(resources, entryOffset, 8))
        {
            return null;
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(resources[(entryOffset + 4)..]);
        return ((int)(raw & ~SubdirectoryFlag), (raw & SubdirectoryFlag) != 0);
    }

    private static bool Contains(ReadOnlySpan<byte> data, int offset, int count)
        => offset >= 0 && count >= 0 && offset <= data.Length - count;
}
