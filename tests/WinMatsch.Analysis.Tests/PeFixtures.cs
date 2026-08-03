using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>The version-resource strings to embed into a test PE.</summary>
public sealed record VersionStrings(
    string? ProductName = null,
    string? CompanyName = null,
    string? LegalCopyright = null,
    string? ProductVersion = null,
    string? FileVersion = null,
    string? OriginalFilename = null,
    string? FileDescription = null);

/// <summary>
/// Builds small but structurally real PE files for analyzer tests using
/// <see cref="ManagedPEBuilder"/> plus a hand-written .rsrc section. The VS_VERSIONINFO
/// binary layout is produced independently per the documented spec, so these fixtures double
/// as a cross-check of the production parser.
/// </summary>
internal static class PeFixtures
{
    public const int RtVersion = 16;
    public const int RtManifest = 24;

    public static byte[] BuildExe(
        Machine machine = Machine.Amd64,
        VersionStrings? version = null,
        string? manifestXml = null,
        bool isDll = false)
    {
        List<(int TypeId, byte[] Data)> resources = [];
        if (version is not null)
        {
            resources.Add((RtVersion, BuildVersionResource(version)));
        }

        if (manifestXml is not null)
        {
            resources.Add((RtManifest, Encoding.UTF8.GetBytes(manifestXml)));
        }

        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString("test.exe"),
            metadata.GetOrAddGuid(new Guid("F47DFB66-3C7A-4A89-8D8C-4E6A8A42C4B3")),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("test"),
            new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        Characteristics characteristics = isDll
            ? Characteristics.ExecutableImage | Characteristics.Dll
            : Characteristics.ExecutableImage;
        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(machine: machine, imageCharacteristics: characteristics),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder(),
            nativeResources: resources.Count == 0 ? null : new ResourceSection(resources),
            deterministicIdProvider: static _ => new BlobContentId(
                new Guid("F47DFB66-3C7A-4A89-8D8C-4E6A8A42C4B3"),
                0x5EED9012));

        var output = new BlobBuilder();
        peBuilder.Serialize(output);
        return output.ToArray();
    }

    public static MemoryStream BuildExeStream(
        Machine machine = Machine.Amd64,
        VersionStrings? version = null,
        string? manifestXml = null)
        => new(BuildExe(machine, version, manifestXml));

    public static string ManifestXml(string level) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
            <security>
              <requestedPrivileges>
                <requestedExecutionLevel level="{level}" uiAccess="false"/>
              </requestedPrivileges>
            </security>
          </trustInfo>
        </assembly>
        """;

    /// <summary>
    /// Writes a VS_VERSIONINFO structure by the book: nested length-prefixed blocks with
    /// UTF-16 keys, values, and 32-bit alignment padding between all parts.
    /// </summary>
    public static byte[] BuildVersionResource(VersionStrings version)
    {
        List<(string Key, string Value)> strings = [];
        Add("ProductName", version.ProductName);
        Add("CompanyName", version.CompanyName);
        Add("LegalCopyright", version.LegalCopyright);
        Add("ProductVersion", version.ProductVersion);
        Add("FileVersion", version.FileVersion);
        Add("OriginalFilename", version.OriginalFilename);
        Add("FileDescription", version.FileDescription);

        byte[][] stringBlocks = new byte[strings.Count][];
        for (int i = 0; i < strings.Count; i++)
        {
            (string key, string value) = strings[i];
            byte[] valueBytes = Encoding.Unicode.GetBytes(value + "\0");
            stringBlocks[i] = BuildBlock(key, (ushort)(value.Length + 1), type: 1, valueBytes);
        }

        byte[] stringTable = BuildBlock("040904B0", valueLength: 0, type: 1, value: [], stringBlocks);
        byte[] stringFileInfo = BuildBlock("StringFileInfo", valueLength: 0, type: 1, value: [], stringTable);
        return BuildBlock("VS_VERSION_INFO", valueLength: 52, type: 0, BuildFixedFileInfo(), stringFileInfo);

        void Add(string key, string? value)
        {
            if (value is not null)
            {
                strings.Add((key, value));
            }
        }
    }

    /// <summary>
    /// One version-info block: wLength, wValueLength, wType, null-terminated UTF-16 key,
    /// padding to 32 bits, the value, then each child preceded by padding to 32 bits.
    /// wLength covers the whole block without trailing padding.
    /// </summary>
    private static byte[] BuildBlock(string key, ushort valueLength, ushort type, byte[] value, params byte[][] children)
    {
        List<byte> bytes = [];
        AddUInt16(bytes, 0); // wLength, patched below.
        AddUInt16(bytes, valueLength);
        AddUInt16(bytes, type);
        bytes.AddRange(Encoding.Unicode.GetBytes(key));
        AddUInt16(bytes, 0); // Key null terminator.
        Pad4(bytes);
        bytes.AddRange(value);
        foreach (byte[] child in children)
        {
            Pad4(bytes);
            bytes.AddRange(child);
        }

        byte[] result = [.. bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)result.Length);
        return result;
    }

    /// <summary>VS_FIXEDFILEINFO: only the signature and structure version matter to the parser.</summary>
    private static byte[] BuildFixedFileInfo()
    {
        byte[] fixedInfo = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo, 0xFEEF04BD);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4), 0x00010000);
        return fixedInfo;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static void Pad4(List<byte> bytes)
    {
        while (bytes.Count % 4 != 0)
        {
            bytes.Add(0);
        }
    }

    /// <summary>
    /// Writes .rsrc section content with one resource per type: root directory → type
    /// directory (name id 1) → name directory (language 0x0409) → data entry → data. All
    /// directory offsets are relative to the section start; the data entry holds an RVA.
    /// Shared with <see cref="BurnFixtures"/>, whose stub PE is built without
    /// <see cref="ManagedPEBuilder"/>.
    /// </summary>
    public static void WriteResourceSection(
        BlobBuilder builder,
        int relativeVirtualAddress,
        List<(int TypeId, byte[] Data)> resources)
    {
        int count = resources.Count;
        int rootSize = 16 + (8 * count);
        int directoriesStart = rootSize;                       // Type + name directory pairs, 48 bytes each.
        int dataEntriesStart = directoriesStart + (count * 48);
        int dataStart = dataEntriesStart + (count * 16);

        int[] dataOffsets = new int[count];
        int offset = dataStart;
        for (int i = 0; i < count; i++)
        {
            offset = (offset + 3) & ~3;
            dataOffsets[i] = offset;
            offset += resources[i].Data.Length;
        }

        WriteDirectoryHeader(builder, count);
        for (int i = 0; i < count; i++)
        {
            builder.WriteUInt32((uint)resources[i].TypeId);
            builder.WriteUInt32(0x80000000u | (uint)(directoriesStart + (i * 48)));
        }

        for (int i = 0; i < count; i++)
        {
            // Type directory: one id entry (resource name id 1) → name directory.
            WriteDirectoryHeader(builder, 1);
            builder.WriteUInt32(1);
            builder.WriteUInt32(0x80000000u | (uint)(directoriesStart + (i * 48) + 24));

            // Name directory: one id entry (language 0x0409) → data entry (leaf).
            WriteDirectoryHeader(builder, 1);
            builder.WriteUInt32(0x0409);
            builder.WriteUInt32((uint)(dataEntriesStart + (i * 16)));
        }

        for (int i = 0; i < count; i++)
        {
            builder.WriteUInt32((uint)(relativeVirtualAddress + dataOffsets[i]));
            builder.WriteUInt32((uint)resources[i].Data.Length);
            builder.WriteUInt32(0); // Code page.
            builder.WriteUInt32(0); // Reserved.
        }

        int written = dataStart;
        for (int i = 0; i < count; i++)
        {
            while (written < dataOffsets[i])
            {
                builder.WriteByte(0);
                written++;
            }

            builder.WriteBytes(resources[i].Data);
            written += resources[i].Data.Length;
        }
    }

    private static void WriteDirectoryHeader(BlobBuilder builder, int idEntryCount)
    {
        builder.WriteUInt32(0); // Characteristics.
        builder.WriteUInt32(0); // Timestamp.
        builder.WriteUInt16(0); // Major version.
        builder.WriteUInt16(0); // Minor version.
        builder.WriteUInt16(0); // Named entry count.
        builder.WriteUInt16((ushort)idEntryCount);
    }

    /// <summary>The <see cref="ManagedPEBuilder"/> adapter over <see cref="WriteResourceSection"/>.</summary>
    private sealed class ResourceSection : ResourceSectionBuilder
    {
        private readonly List<(int TypeId, byte[] Data)> _resources;

        public ResourceSection(List<(int TypeId, byte[] Data)> resources) => _resources = resources;

        protected override void Serialize(BlobBuilder builder, SectionLocation location)
            => WriteResourceSection(builder, location.RelativeVirtualAddress, _resources);
    }
}
