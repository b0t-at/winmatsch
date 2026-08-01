using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Dependencies;

internal sealed record PeImportInspection(
    Architecture? Architecture,
    IReadOnlyList<string> ImportedModules,
    bool IsManaged,
    bool IsComplete);

/// <summary>Reads only PE headers, import descriptors, and bounded module names from a seekable stream.</summary>
internal static class PeImportReader
{
    private const int ImportDescriptorSize = 20;
    private const int ImportDescriptorNameOffset = 12;
    private const uint IlOnly = 0x00000001;
    private const uint Requires32Bit = 0x00000002;

    public static PeImportInspection Inspect(
        Stream stream,
        int maximumDescriptors,
        int maximumNameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            return Incomplete();
        }

        long savedPosition = stream.Position;
        try
        {
            Span<byte> dos = stackalloc byte[64];
            if (!TryReadAt(stream, 0, dos)
                || dos[0] != (byte)'M'
                || dos[1] != (byte)'Z')
            {
                return Incomplete();
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
            Span<byte> coff = stackalloc byte[24];
            if (peOffset < 64
                || !TryReadAt(stream, peOffset, coff)
                || BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550)
            {
                return Incomplete();
            }

            Machine machine = (Machine)BinaryPrimitives.ReadUInt16LittleEndian(coff[4..]);
            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff[6..]);
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[20..]);
            Architecture? architecture = MapArchitecture(machine);
            if (sectionCount is <= 0 or > AnalysisLimits.MaxPeSections
                || optionalSize is < 96 or > 4096)
            {
                return new PeImportInspection(architecture, [], false, false);
            }

            byte[] optional = new byte[optionalSize];
            if (!TryReadAt(stream, peOffset + 24L, optional))
            {
                return new PeImportInspection(architecture, [], false, false);
            }

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
            int dataDirectoryOffset;
            int numberOfDirectoriesOffset;
            if (magic == 0x10B)
            {
                dataDirectoryOffset = 96;
                numberOfDirectoriesOffset = 92;
            }
            else if (magic == 0x20B)
            {
                dataDirectoryOffset = 112;
                numberOfDirectoriesOffset = 108;
            }
            else
            {
                return new PeImportInspection(architecture, [], false, false);
            }

            if (optional.Length < numberOfDirectoriesOffset + 4)
            {
                return new PeImportInspection(architecture, [], false, false);
            }

            uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(60));
            uint directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(numberOfDirectoriesOffset));
            Section[] sections = ReadSections(stream, peOffset + 24L + optionalSize, sectionCount);

            bool isManaged = false;
            if (directoryCount > 14
                && TryReadDirectory(optional, dataDirectoryOffset, 14, out uint clrRva, out uint clrSize)
                && clrRva != 0
                && clrSize >= 20
                && TryMapRva(clrRva, sizeOfHeaders, sections, stream.Length, out long clrOffset, out long clrAvailable))
            {
                Span<byte> clrPrefix = stackalloc byte[20];
                if (clrAvailable >= clrPrefix.Length && TryReadAt(stream, clrOffset, clrPrefix))
                {
                    isManaged = true;
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(clrPrefix[16..]);
                    if (machine == Machine.I386
                        && (flags & IlOnly) != 0
                        && (flags & Requires32Bit) == 0)
                    {
                        architecture = Architecture.Neutral;
                    }
                }
            }

            if (directoryCount <= 1
                || !TryReadDirectory(optional, dataDirectoryOffset, 1, out uint importRva, out uint importSize))
            {
                return new PeImportInspection(architecture, [], isManaged, true);
            }

            if (importRva == 0 || importSize == 0)
            {
                return new PeImportInspection(architecture, [], isManaged, true);
            }

            if (!TryMapRva(importRva, sizeOfHeaders, sections, stream.Length, out long descriptorOffset, out long descriptorAvailable))
            {
                return new PeImportInspection(architecture, [], isManaged, false);
            }

            int declaredDescriptors = checked((int)Math.Min(importSize / ImportDescriptorSize, int.MaxValue));
            int descriptorCount = Math.Min(declaredDescriptors, maximumDescriptors);
            if (descriptorCount <= 0)
            {
                return new PeImportInspection(architecture, [], isManaged, false);
            }

            var modules = new List<string>();
            Span<byte> descriptor = stackalloc byte[ImportDescriptorSize];
            for (int i = 0; i < descriptorCount; i++)
            {
                long relative = i * (long)ImportDescriptorSize;
                if (relative > descriptorAvailable - ImportDescriptorSize
                    || !TryReadAt(stream, descriptorOffset + relative, descriptor))
                {
                    return new PeImportInspection(architecture, modules, isManaged, false);
                }

                if (descriptor.IndexOfAnyExcept((byte)0) < 0)
                {
                    return new PeImportInspection(architecture, modules, isManaged, true);
                }

                uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[ImportDescriptorNameOffset..]);
                if (nameRva == 0
                    || !TryMapRva(nameRva, sizeOfHeaders, sections, stream.Length, out long nameOffset, out long nameAvailable)
                    || !TryReadAsciiName(stream, nameOffset, nameAvailable, maximumNameBytes, out string module))
                {
                    return new PeImportInspection(architecture, modules, isManaged, false);
                }

                modules.Add(module);
            }

            return new PeImportInspection(architecture, modules, isManaged, false);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or OverflowException)
        {
            return Incomplete();
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private static Section[] ReadSections(Stream stream, long tableOffset, int sectionCount)
    {
        var sections = new Section[sectionCount];
        Span<byte> header = stackalloc byte[40];
        for (int i = 0; i < sectionCount; i++)
        {
            if (!TryReadAt(stream, tableOffset + (i * 40L), header))
            {
                throw new EndOfStreamException();
            }

            sections[i] = new Section(
                BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
        }

        return sections;
    }

    private static bool TryReadDirectory(
        ReadOnlySpan<byte> optional,
        int directoryOffset,
        int index,
        out uint rva,
        out uint size)
    {
        int offset = directoryOffset + (index * 8);
        if (offset < 0 || offset > optional.Length - 8)
        {
            rva = size = 0;
            return false;
        }

        rva = BinaryPrimitives.ReadUInt32LittleEndian(optional[offset..]);
        size = BinaryPrimitives.ReadUInt32LittleEndian(optional[(offset + 4)..]);
        return true;
    }

    private static bool TryMapRva(
        uint rva,
        uint sizeOfHeaders,
        IReadOnlyList<Section> sections,
        long streamLength,
        out long offset,
        out long available)
    {
        if (rva < sizeOfHeaders && rva < streamLength)
        {
            offset = rva;
            available = Math.Min(sizeOfHeaders - (long)rva, streamLength - rva);
            return available > 0;
        }

        foreach (Section section in sections)
        {
            long mappedSize = Math.Max(section.VirtualSize, section.RawSize);
            long delta = rva - (long)section.VirtualAddress;
            if (delta < 0 || delta >= mappedSize || delta >= section.RawSize)
            {
                continue;
            }

            offset = section.RawOffset + delta;
            available = Math.Min(section.RawSize - delta, streamLength - offset);
            return offset >= 0 && available > 0;
        }

        offset = available = 0;
        return false;
    }

    private static bool TryReadAsciiName(
        Stream stream,
        long offset,
        long available,
        int maximumNameBytes,
        out string name)
    {
        name = "";
        int length = (int)Math.Min(available, maximumNameBytes + 1L);
        if (length <= 1)
        {
            return false;
        }

        byte[] bytes = new byte[length];
        if (!TryReadAt(stream, offset, bytes))
        {
            return false;
        }

        int terminator = bytes.AsSpan().IndexOf((byte)0);
        if (terminator <= 0 || terminator > maximumNameBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> value = bytes.AsSpan(0, terminator);
        if (value.IndexOfAnyInRange((byte)0x00, (byte)0x20) >= 0
            || value.IndexOfAnyInRange((byte)0x7F, byte.MaxValue) >= 0)
        {
            return false;
        }

        name = Encoding.ASCII.GetString(value);
        return true;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            return false;
        }

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }

    private static Architecture? MapArchitecture(Machine machine)
        => machine switch
        {
            Machine.Amd64 => Architecture.X64,
            Machine.I386 => Architecture.X86,
            Machine.Arm64 => Architecture.Arm64,
            Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => Architecture.Arm,
            _ => null,
        };

    private static PeImportInspection Incomplete()
        => new(null, [], false, false);

    private readonly record struct Section(
        uint VirtualSize,
        uint VirtualAddress,
        uint RawSize,
        uint RawOffset);
}
