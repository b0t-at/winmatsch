using System.Buffers.Binary;

namespace WinMatsch.Analysis.Burn;

/// <summary>
/// The Burn container header found at the start of the <c>.wixburn</c> PE section, matching
/// the engine's <c>BURN_SECTION_HEADER</c> struct (burn/engine/section.cpp; the layout is
/// identical in WiX v3 and WiX v4+). All fields are little-endian:
/// <code>
/// offset  0  uint32  magic (0x00f14300)
/// offset  4  uint32  version (2)
/// offset  8  Guid    bundle id (16 bytes)
/// offset 24  uint32  stub size — the file offset where container 0 (the UX container) starts
/// offset 28  uint32  original checksum
/// offset 32  uint32  original signature offset
/// offset 36  uint32  original signature size
/// offset 40  uint32  container format (1 = cabinet)
/// offset 44  uint32  container count
/// offset 48  uint32  container sizes, one per container
/// </code>
/// The UX container sits at <see cref="StubSize"/>; attached payload containers follow the
/// engine (stub + UX container) sequentially.
/// </summary>
internal sealed class BurnSectionHeader
{
    /// <summary>The <c>BURN_SECTION_MAGIC</c> value.</summary>
    public const uint Magic = 0x00f14300;

    /// <summary>The <c>BURN_SECTION_VERSION</c> value written by WiX v3 and v4+.</summary>
    public const uint SupportedVersion = 2;

    /// <summary>The only container format Burn defines: a cabinet archive.</summary>
    public const uint CabinetContainerFormat = 1;

    private const int FixedHeaderSize = 48;

    /// <summary>The bundle id GUID stamped into the header at build time.</summary>
    public required Guid BundleId { get; init; }

    /// <summary>The size of the stub image, which is also the file offset of the UX container.</summary>
    public required uint StubSize { get; init; }

    /// <summary>The container sizes in bytes; index 0 is the UX container.</summary>
    public required IReadOnlyList<uint> ContainerSizes { get; init; }

    /// <summary>
    /// Parses the header from the raw <c>.wixburn</c> section data. Returns null when the
    /// data does not start with the Burn magic (the section belongs to something else).
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The magic matched but the header is truncated, has an unsupported version or container
    /// format, or declares no containers.
    /// </exception>
    public static BurnSectionHeader? Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(section) != Magic)
        {
            return null;
        }

        if (section.Length < FixedHeaderSize)
        {
            throw new InvalidDataException("The Burn .wixburn section is smaller than the container header.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);
        if (version != SupportedVersion)
        {
            throw new InvalidDataException($"The Burn container header declares unsupported version {version}.");
        }

        uint format = BinaryPrimitives.ReadUInt32LittleEndian(section[40..]);
        if (format != CabinetContainerFormat)
        {
            throw new InvalidDataException($"The Burn container header declares unsupported container format {format}.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(section[44..]);
        if (count == 0)
        {
            throw new InvalidDataException("The Burn container header declares no containers; the UX container is missing.");
        }

        if (count > (uint)(section.Length - FixedHeaderSize) / 4)
        {
            throw new InvalidDataException("The Burn container header declares more container sizes than the section holds.");
        }

        uint[] sizes = new uint[count];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(section[(FixedHeaderSize + (i * 4))..]);
        }

        return new BurnSectionHeader
        {
            BundleId = new Guid(section.Slice(8, 16)),
            StubSize = BinaryPrimitives.ReadUInt32LittleEndian(section[24..]),
            ContainerSizes = sizes,
        };
    }
}
