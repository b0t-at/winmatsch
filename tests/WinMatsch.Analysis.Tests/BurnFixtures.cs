using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Builds small but structurally real WiX Burn bundles for tests: a native PE emitted with
/// <see cref="PEBuilder"/> carrying a .wixburn section with the documented burn container
/// header, followed by the attached UX cabinet at the header's stub size. The cabinet writer
/// is implemented by the book (CFHEADER, CFFOLDER, CFFILE, CFDATA) independently from the
/// production reader, so the fixtures double as a cross-check — same as
/// <see cref="MsiFixtures"/> does for the MSI reader.
/// </summary>
internal static class BurnFixtures
{
    public const string Wix3Namespace = "http://schemas.microsoft.com/wix/2008/Burn";
    public const string Wix4Namespace = "http://wixtoolset.org/schemas/v4/2008/Burn";

    public const string BundleProductCode = "{4C69E8B5-A9C6-4E42-9D3D-3F1F0A2E5C11}";
    public const string BundleUpgradeCode = "{9A6BF3A0-7A5E-4E19-8C29-2A1F5D40D8B7}";
    public const string DefaultArpXml =
        """<Arp Register="yes" DisplayName="Contoso Suite" DisplayVersion="2.5.0" Publisher="Contoso Ltd" />""";

    private const uint BurnMagic = 0x00f14300;
    private const int MaxBlockSize = 32768;

    private static readonly Guid _bundleId = new("{E2A93F0B-3D44-4B1C-9A55-6F0C8E1D27A0}");

    /// <summary>Composes a Burn manifest like the WiX toolset writes into UX container file "0".</summary>
    public static string ManifestXml(
        string xmlns = Wix3Namespace,
        string? arpXml = DefaultArpXml,
        bool includeRelatedBundle = true,
        string? registrationVersion = "2.5.0.0",
        string? installCondition = null,
        string? msiPackageXml = null)
    {
        string versionAttribute = registrationVersion is null ? "" : $" Version=\"{registrationVersion}\"";
        string relatedBundle = includeRelatedBundle
            ? $"<RelatedBundle Id=\"{BundleUpgradeCode}\" Action=\"Upgrade\" />"
            : "";
        string conditionAttribute = installCondition is null ? "" : $" InstallCondition=\"{installCondition}\"";
        msiPackageXml ??=
            $"<MsiPackage Id=\"MainPackage\" ProductCode=\"{{11111111-2222-3333-4444-555555555555}}\" "
            + $"Version=\"2.5.0\"{conditionAttribute} />";

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <BurnManifest xmlns="{{xmlns}}">
              <Registration Id="{{BundleProductCode}}" ExecutableName="setup.exe" PerMachine="yes"{{versionAttribute}}>
                {{arpXml}}
              </Registration>
              {{relatedBundle}}
              <Chain>
                {{msiPackageXml}}
              </Chain>
            </BurnManifest>
            """;
    }

    /// <summary>
    /// Builds a complete bundle: the stub PE with the .wixburn section plus the attached UX
    /// cabinet containing <paramref name="manifestXml"/> as file "0". The corrupt-structure
    /// parameters exist so tests can produce positively-identified-but-broken bundles.
    /// </summary>
    public static byte[] BuildBundle(
        string manifestXml,
        Machine machine = Machine.I386,
        bool msZip = false,
        uint magic = BurnMagic,
        uint sectionVersion = 2,
        uint containerFormat = 1,
        uint[]? containerSizes = null,
        byte[]? uxContainer = null,
        VersionStrings? version = null,
        string? appManifestXml = null)
    {
        byte[] cabinet = uxContainer ?? BuildCabinet([("0", Encoding.UTF8.GetBytes(manifestXml))], msZip);
        uint[] sizes = containerSizes ?? [(uint)cabinet.Length];

        List<(int TypeId, byte[] Data)> resources = [];
        if (version is not null)
        {
            resources.Add((PeFixtures.RtVersion, PeFixtures.BuildVersionResource(version)));
        }

        if (appManifestXml is not null)
        {
            resources.Add((PeFixtures.RtManifest, Encoding.UTF8.GetBytes(appManifestXml)));
        }

        // The header stores the stub size (= offset of the attached UX cabinet), which is only
        // known after serialization. The header is fixed-size, so serialize once with a
        // placeholder to learn the length, then again with the real value.
        byte[] placeholder = SerializeStub(machine, BuildBurnSectionData(magic, sectionVersion, containerFormat, 0, sizes), resources);
        byte[] stub = SerializeStub(
            machine,
            BuildBurnSectionData(magic, sectionVersion, containerFormat, (uint)placeholder.Length, sizes),
            resources);
        if (stub.Length != placeholder.Length)
        {
            throw new InvalidOperationException("The stub image changed size between serialization passes.");
        }

        return [.. stub, .. cabinet];
    }

    /// <summary>
    /// Writes a single-folder cabinet holding the given files, split into CFDATA blocks of at
    /// most 32 KiB uncompressed. MSZIP blocks are independent deflate streams — the format
    /// permits a compressor not to reference earlier blocks, while readers must still support
    /// history. CFDATA checksums are written as zero (not computed).
    /// </summary>
    public static byte[] BuildCabinet(
        (string Name, byte[] Data)[] files,
        bool msZip = false,
        ushort? compressionTypeOverride = null)
    {
        byte[] folderData = files.SelectMany(file => file.Data).ToArray();
        var blocks = new List<(byte[] Payload, int UncompressedLength)>();
        for (int offset = 0; offset < folderData.Length; offset += MaxBlockSize)
        {
            byte[] chunk = folderData[offset..Math.Min(offset + MaxBlockSize, folderData.Length)];
            blocks.Add((msZip ? CompressMsZipBlock(chunk) : chunk, chunk.Length));
        }

        int filesOffset = 36 + 8; // CFHEADER (no reserve) + one CFFOLDER.
        int filesSize = files.Sum(file => 16 + Encoding.UTF8.GetByteCount(file.Name) + 1);
        int dataOffset = filesOffset + filesSize;
        int totalSize = dataOffset + blocks.Sum(block => 8 + block.Payload.Length);

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        // CFHEADER: signature, reserved, cabinet size, reserved, first CFFILE offset,
        // reserved, minor/major version, folder/file counts, flags, set id, cabinet index.
        writer.Write("MSCF"u8);
        writer.Write(0u);
        writer.Write((uint)totalSize);
        writer.Write(0u);
        writer.Write((uint)filesOffset);
        writer.Write(0u);
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)1);
        writer.Write((ushort)files.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0x0622);
        writer.Write((ushort)0);

        // CFFOLDER: first CFDATA offset, CFDATA count, compression type.
        writer.Write((uint)dataOffset);
        writer.Write((ushort)blocks.Count);
        writer.Write(compressionTypeOverride ?? (ushort)(msZip ? 1 : 0));

        // CFFILE: uncompressed size, offset within the folder's uncompressed data, folder
        // index, DOS date/time, attributes, null-terminated name.
        int folderOffset = 0;
        foreach ((string name, byte[] data) in files)
        {
            writer.Write((uint)data.Length);
            writer.Write((uint)folderOffset);
            writer.Write((ushort)0);
            writer.Write((ushort)0x5A8C);
            writer.Write((ushort)0x60A0);
            writer.Write((ushort)0x20);
            writer.Write(Encoding.UTF8.GetBytes(name));
            writer.Write((byte)0);
            folderOffset += data.Length;
        }

        // CFDATA: checksum (zero = not computed), compressed and uncompressed byte counts.
        foreach ((byte[] payload, int uncompressedLength) in blocks)
        {
            writer.Write(0u);
            writer.Write((ushort)payload.Length);
            writer.Write((ushort)uncompressedLength);
            writer.Write(payload);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>One MSZIP block: the "CK" signature followed by a raw deflate stream.</summary>
    private static byte[] CompressMsZipBlock(byte[] chunk)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'C');
        buffer.WriteByte((byte)'K');
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(chunk);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The burn container header per WiX's BURN_SECTION_HEADER: magic, version, bundle id
    /// GUID, stub size, original checksum/signature info (zero here), container format,
    /// container count, and one uint32 size per container.
    /// </summary>
    private static byte[] BuildBurnSectionData(uint magic, uint version, uint containerFormat, uint stubSize, uint[] containerSizes)
    {
        byte[] data = new byte[48 + (4 * containerSizes.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
        _bundleId.TryWriteBytes(data.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), stubSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), containerFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), (uint)containerSizes.Length);
        for (int i = 0; i < containerSizes.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48 + (i * 4)), containerSizes[i]);
        }

        return data;
    }

    private static byte[] SerializeStub(Machine machine, byte[] wixburnData, List<(int TypeId, byte[] Data)> resources)
    {
        var builder = new StubBuilder(machine, wixburnData, resources);
        var output = new BlobBuilder();
        builder.Serialize(output);
        return output.ToArray();
    }

    /// <summary>
    /// A native (unmanaged) PE with a .wixburn section and, when resources are given, a
    /// .rsrc section reusing <see cref="PeFixtures.WriteResourceSection"/>.
    /// </summary>
    private sealed class StubBuilder : PEBuilder
    {
        private readonly PEDirectoriesBuilder _directories = new();
        private readonly byte[] _wixburnData;
        private readonly List<(int TypeId, byte[] Data)> _resources;

        public StubBuilder(Machine machine, byte[] wixburnData, List<(int TypeId, byte[] Data)> resources)
            : base(
                new PEHeaderBuilder(
                    machine: machine,
                    imageCharacteristics: Characteristics.ExecutableImage),
                deterministicIdProvider: static _ => new BlobContentId(
                    new Guid("28FF516A-D0D2-4B2A-9DF6-A07D50AC0D90"),
                    0x5EED5678))
        {
            _wixburnData = wixburnData;
            _resources = resources;
        }

        protected override ImmutableArray<Section> CreateSections()
        {
            ImmutableArray<Section>.Builder sections = ImmutableArray.CreateBuilder<Section>();
            sections.Add(new Section(".wixburn", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead));
            if (_resources.Count > 0)
            {
                sections.Add(new Section(".rsrc", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead));
            }

            return sections.ToImmutable();
        }

        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            var builder = new BlobBuilder();
            if (name == ".wixburn")
            {
                builder.WriteBytes(_wixburnData);
            }
            else
            {
                PeFixtures.WriteResourceSection(builder, location.RelativeVirtualAddress, _resources);
                _directories.ResourceTable = new DirectoryEntry(location.RelativeVirtualAddress, builder.Count);
            }

            return builder;
        }

        protected override PEDirectoriesBuilder GetDirectories() => _directories;
    }
}
