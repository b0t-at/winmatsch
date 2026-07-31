using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Dependencies;

internal sealed record PeImportInspection(
    Architecture? Architecture,
    IReadOnlyList<string> ImportedModules,
    bool IsComplete);

internal static class PeImportReader
{
    private const int ImportDescriptorSize = 20;
    private const int ImportDescriptorNameOffset = 12;

    public static PeImportInspection Inspect(
        byte[] image,
        int maximumDescriptors,
        int maximumNameBytes)
    {
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var reader = new PEReader(stream);
            PEHeaders headers = reader.PEHeaders;
            Architecture? architecture = MapArchitecture(headers);
            PEHeader? peHeader = headers.PEHeader;
            if (peHeader is null)
            {
                return new PeImportInspection(architecture, [], false);
            }

            DirectoryEntry imports = peHeader.ImportTableDirectory;
            if (imports.RelativeVirtualAddress == 0 || imports.Size == 0)
            {
                return new PeImportInspection(architecture, [], true);
            }

            PEMemoryBlock block = reader.GetSectionData(imports.RelativeVirtualAddress);
            ReadOnlySpan<byte> descriptors = block.GetContent().AsSpan();
            int declaredDescriptorCount = imports.Size / ImportDescriptorSize;
            if (declaredDescriptorCount <= 0)
            {
                return new PeImportInspection(architecture, [], false);
            }

            int descriptorCount = Math.Min(declaredDescriptorCount, maximumDescriptors);
            var modules = new List<string>();
            for (int i = 0; i < descriptorCount; i++)
            {
                int offset = i * ImportDescriptorSize;
                if (offset + ImportDescriptorSize > descriptors.Length)
                {
                    return new PeImportInspection(architecture, modules, false);
                }

                ReadOnlySpan<byte> descriptor = descriptors.Slice(offset, ImportDescriptorSize);
                if (descriptor.IndexOfAnyExcept((byte)0) < 0)
                {
                    return new PeImportInspection(architecture, modules, true);
                }

                int nameRva = BinaryPrimitives.ReadInt32LittleEndian(descriptor[ImportDescriptorNameOffset..]);
                if (nameRva <= 0 || !TryReadAsciiName(reader, nameRva, maximumNameBytes, out string module))
                {
                    return new PeImportInspection(architecture, modules, false);
                }

                modules.Add(module);
            }

            return new PeImportInspection(architecture, modules, false);
        }
        catch (BadImageFormatException)
        {
            return new PeImportInspection(null, [], false);
        }
        catch (IOException)
        {
            return new PeImportInspection(null, [], false);
        }
    }

    private static bool TryReadAsciiName(
        PEReader reader,
        int relativeVirtualAddress,
        int maximumNameBytes,
        out string name)
    {
        name = "";
        try
        {
            ReadOnlySpan<byte> content = reader.GetSectionData(relativeVirtualAddress).GetContent().AsSpan();
            int inspectedLength = Math.Min(content.Length, maximumNameBytes + 1);
            int terminator = content[..inspectedLength].IndexOf((byte)0);
            if (terminator <= 0 || terminator > maximumNameBytes)
            {
                return false;
            }

            ReadOnlySpan<byte> bytes = content[..terminator];
            foreach (byte value in bytes)
            {
                if (value is < 0x21 or > 0x7e)
                {
                    return false;
                }
            }

            name = Encoding.ASCII.GetString(bytes);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static Architecture? MapArchitecture(PEHeaders headers)
    {
        if (headers.CoffHeader.Machine == Machine.I386
            && headers.CorHeader is { } corHeader
            && (corHeader.Flags & CorFlags.ILOnly) != 0
            && (corHeader.Flags & CorFlags.Requires32Bit) == 0)
        {
            return Architecture.Neutral;
        }

        return headers.CoffHeader.Machine switch
        {
            Machine.Amd64 => Architecture.X64,
            Machine.I386 => Architecture.X86,
            Machine.Arm64 => Architecture.Arm64,
            Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => Architecture.Arm,
            _ => null,
        };
    }
}
