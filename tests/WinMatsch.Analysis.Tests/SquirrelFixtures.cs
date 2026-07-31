using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Independently encodes classic Squirrel's named DATA/131 resource and Clowd.Squirrel's
/// in-image bundle locator. Zips are hand-written with stored entries.
/// </summary>
internal static class SquirrelFixtures
{
    /// <summary>Version strings resembling the branded Squirrel bootstrap stub.</summary>
    public static VersionStrings BrandedStub { get; } = new(
        ProductName: "Contoso Chat",
        CompanyName: "Contoso Ltd",
        ProductVersion: "1.0.0",
        FileDescription: "Squirrel Setup",
        OriginalFilename: "SquirrelSetup.exe");

    /// <summary>A nuspec manifest with the identity fields Squirrel copies into ARP.</summary>
    public static string NuspecXml(
        string? id = "Contoso.Chat",
        string? version = "1.2.3",
        string? title = "Contoso Chat",
        string? authors = "Contoso Ltd")
    {
        var metadata = new StringBuilder();
        if (id is not null)
        {
            metadata.Append("<id>").Append(id).Append("</id>");
        }

        if (version is not null)
        {
            metadata.Append("<version>").Append(version).Append("</version>");
        }

        if (title is not null)
        {
            metadata.Append("<title>").Append(title).Append("</title>");
        }

        if (authors is not null)
        {
            metadata.Append("<authors>").Append(authors).Append("</authors>");
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
              <metadata>{metadata}</metadata>
            </package>
            """;
    }

    /// <summary>Builds a release nupkg: a zip with a root nuspec plus payload entries.</summary>
    public static byte[] BuildNupkg(string nuspecXml, string nuspecName = "Contoso.Chat.nuspec", params (string Name, byte[] Data)[] extraEntries)
    {
        (string, byte[])[] entries = [(nuspecName, Encoding.UTF8.GetBytes(nuspecXml)), .. extraEntries];
        return BuildStoredZip(entries);
    }

    private static readonly byte[] _clowdBundleSignature =
    [
        0x94, 0xF0, 0xB1, 0x7B, 0x68, 0x93, 0xE0, 0x29,
        0x37, 0xEB, 0x34, 0xEF, 0x53, 0xAA, 0xE7, 0xD4,
        0x2B, 0x54, 0xF5, 0x70, 0x7E, 0xF5, 0xD6, 0xF5,
        0x78, 0x54, 0x98, 0x3E, 0x5E, 0x94, 0xED, 0x7D,
    ];

    /// <summary>Builds a classic Squirrel Setup.exe with the release zip in DATA resource 131.</summary>
    public static byte[] BuildClassicSetup(
        byte[] nupkg,
        string nupkgName = "Contoso.Chat-1.2.3-full.nupkg",
        Machine machine = Machine.I386)
        => BuildResourceSetup(
            BuildStoredZip(
                [("RELEASES", Encoding.UTF8.GetBytes("stub-releases-index")), (nupkgName, nupkg)]),
            typeName: "DATA",
            resourceId: 131,
            machine);

    /// <summary>Builds a Clowd.Squirrel setup with an in-image locator bounding the appended nupkg.</summary>
    public static byte[] BuildClowdSetup(
        byte[] nupkg,
        Machine machine = Machine.I386)
    {
        byte[] marker = new byte[16 + _clowdBundleSignature.Length];
        _clowdBundleSignature.CopyTo(marker, 16);
        byte[] stub = BuildPeWithNamedResource("BUNDLE", 1, marker, machine);
        int signatureOffset = stub.AsSpan().IndexOf(_clowdBundleSignature);
        BinaryPrimitives.WriteInt64LittleEndian(stub.AsSpan(signatureOffset - 16), stub.Length);
        BinaryPrimitives.WriteInt64LittleEndian(stub.AsSpan(signatureOffset - 8), nupkg.Length);
        return AdvancedInstallerFixtures.Concat(stub, nupkg);
    }

    public static byte[] BuildResourceSetup(
        byte[] payload,
        string typeName,
        int resourceId,
        Machine machine = Machine.I386)
        => BuildPeWithNamedResource(typeName, resourceId, payload, machine);

    public static byte[] BuildDirectoryBomb(ushort entryCount, uint centralDirectorySize = 0)
    {
        byte[] zip = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(zip, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(8), entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(10), entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(12), centralDirectorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(16), 0);
        return zip;
    }

    /// <summary>
    /// Hand-writes a zip with stored (method 0) entries. When
    /// <paramref name="declaredSizeOverrideForLastEntry"/> is set, the last entry's central
    /// directory record announces that uncompressed size instead of the real one — simulating
    /// a hostile archive lying about its payload size.
    /// </summary>
    public static byte[] BuildStoredZip(
        (string Name, byte[] Data)[] entries,
        long? declaredSizeOverrideForLastEntry = null)
    {
        var output = new MemoryStream();
        var centralDirectory = new MemoryStream();

        for (int i = 0; i < entries.Length; i++)
        {
            (string name, byte[] data) = entries[i];
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint crc = SevenZipFixtures.Crc32(data);
            long localHeaderOffset = output.Position;
            uint declaredSize = i == entries.Length - 1 && declaredSizeOverrideForLastEntry is { } declared
                ? (uint)Math.Min(declared, uint.MaxValue)
                : (uint)data.Length;

            // Local file header.
            WriteUInt32(output, 0x04034B50);
            WriteUInt16(output, 20); // version needed
            WriteUInt16(output, 0); // flags
            WriteUInt16(output, 0); // method: stored
            WriteUInt16(output, 0); // time
            WriteUInt16(output, 0); // date
            WriteUInt32(output, crc);
            WriteUInt32(output, (uint)data.Length); // compressed size
            WriteUInt32(output, (uint)data.Length); // uncompressed size
            WriteUInt16(output, (ushort)nameBytes.Length);
            WriteUInt16(output, 0); // extra length
            output.Write(nameBytes);
            output.Write(data);

            // Central directory record.
            WriteUInt32(centralDirectory, 0x02014B50);
            WriteUInt16(centralDirectory, 20); // version made by
            WriteUInt16(centralDirectory, 20); // version needed
            WriteUInt16(centralDirectory, 0); // flags
            WriteUInt16(centralDirectory, 0); // method: stored
            WriteUInt16(centralDirectory, 0); // time
            WriteUInt16(centralDirectory, 0); // date
            WriteUInt32(centralDirectory, crc);
            WriteUInt32(centralDirectory, (uint)data.Length); // compressed size
            WriteUInt32(centralDirectory, declaredSize); // uncompressed size (possibly a lie)
            WriteUInt16(centralDirectory, (ushort)nameBytes.Length);
            WriteUInt16(centralDirectory, 0); // extra length
            WriteUInt16(centralDirectory, 0); // comment length
            WriteUInt16(centralDirectory, 0); // disk number
            WriteUInt16(centralDirectory, 0); // internal attributes
            WriteUInt32(centralDirectory, 0); // external attributes
            WriteUInt32(centralDirectory, (uint)localHeaderOffset);
            centralDirectory.Write(nameBytes);
        }

        long centralDirectoryOffset = output.Position;
        centralDirectory.WriteTo(output);

        // End of central directory.
        WriteUInt32(output, 0x06054B50);
        WriteUInt16(output, 0); // disk number
        WriteUInt16(output, 0); // central directory disk
        WriteUInt16(output, (ushort)entries.Length);
        WriteUInt16(output, (ushort)entries.Length);
        WriteUInt32(output, (uint)centralDirectory.Length);
        WriteUInt32(output, (uint)centralDirectoryOffset);
        WriteUInt16(output, 0); // comment length

        return output.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static byte[] BuildPeWithNamedResource(
        string typeName,
        int resourceId,
        byte[] resourceData,
        Machine machine)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString("setup.exe"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("setup"),
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

        var builder = new ManagedPEBuilder(
            new PEHeaderBuilder(machine: machine, imageCharacteristics: Characteristics.ExecutableImage),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder(),
            nativeResources: new NamedResourceSection(typeName, resourceId, resourceData));
        var output = new BlobBuilder();
        builder.Serialize(output);
        return output.ToArray();
    }

    private sealed class NamedResourceSection(string typeName, int resourceId, byte[] data) : ResourceSectionBuilder
    {
        protected override void Serialize(BlobBuilder builder, SectionLocation location)
        {
            const int typeDirectoryOffset = 24;
            const int nameDirectoryOffset = 48;
            const int dataEntryOffset = 72;
            const int typeNameOffset = 88;
            int typeNameBytes = 2 + (typeName.Length * 2);
            int dataOffset = (typeNameOffset + typeNameBytes + 3) & ~3;

            WriteDirectoryHeader(builder, namedCount: 1, idCount: 0);
            builder.WriteUInt32(0x80000000u | typeNameOffset);
            builder.WriteUInt32(0x80000000u | typeDirectoryOffset);

            WriteDirectoryHeader(builder, namedCount: 0, idCount: 1);
            builder.WriteUInt32((uint)resourceId);
            builder.WriteUInt32(0x80000000u | nameDirectoryOffset);

            WriteDirectoryHeader(builder, namedCount: 0, idCount: 1);
            builder.WriteUInt32(0x0409);
            builder.WriteUInt32(dataEntryOffset);

            builder.WriteUInt32((uint)(location.RelativeVirtualAddress + dataOffset));
            builder.WriteUInt32((uint)data.Length);
            builder.WriteUInt32(0);
            builder.WriteUInt32(0);

            builder.WriteUInt16((ushort)typeName.Length);
            builder.WriteBytes(Encoding.Unicode.GetBytes(typeName));
            while (builder.Count < dataOffset)
            {
                builder.WriteByte(0);
            }

            builder.WriteBytes(data);
        }

        private static void WriteDirectoryHeader(BlobBuilder builder, ushort namedCount, ushort idCount)
        {
            builder.WriteUInt32(0);
            builder.WriteUInt32(0);
            builder.WriteUInt16(0);
            builder.WriteUInt16(0);
            builder.WriteUInt16(namedCount);
            builder.WriteUInt16(idCount);
        }
    }
}
