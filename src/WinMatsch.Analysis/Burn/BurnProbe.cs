using System.Buffers.Binary;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Burn;

/// <summary>
/// Detects WiX Burn bundles: executables with a <c>.wixburn</c> PE section whose data starts
/// with the Burn container header (<see cref="BurnSectionHeader"/>). The UX container — a
/// cabinet attached at the header's stub size — carries the bundle manifest as a file named
/// "0" (<see cref="BurnManifest"/>), which provides the ARP product code, display strings and
/// upgrade code. The architecture defaults to the stub's PE machine, except that a chain
/// package InstallCondition targeting ARM64 promotes the bundle to ARM64: Burn stubs are
/// x86 regardless of the payload they install.
/// </summary>
public sealed class BurnProbe : IExeFormatProbe
{
    // IMAGE_SECTION_HEADER is 40 bytes; the 8-byte Name field exactly fits ".wixburn".
    private const int SectionHeaderSize = 40;
    private static ReadOnlySpan<byte> WixburnSectionName => ".wixburn"u8;

    /// <summary>
    /// Returns the bundle's analysis, or null when the executable has no <c>.wixburn</c>
    /// section starting with the Burn magic.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is positively a Burn bundle but its container structure or manifest is corrupt.
    /// </exception>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        byte[]? section = ReadWixburnSection(stream);
        if (section is null)
        {
            return null;
        }

        BurnSectionHeader? header = BurnSectionHeader.Parse(section);
        if (header is null)
        {
            return null;
        }

        byte[]? manifestBytes = CabinetReader.ReadFile(ReadUxContainer(stream, header), "0");
        if (manifestBytes is null)
        {
            throw new InvalidDataException("The Burn UX container does not contain the manifest file \"0\".");
        }

        BurnManifest manifest = BurnManifest.Parse(manifestBytes);
        VersionInfo version = peFile.VersionInfo;

        var installer = new Installer
        {
            Architecture = manifest.ChainTargetsArm64 ? Architecture.Arm64 : peFile.Architecture,
            InstallerType = InstallerType.Burn,
            ElevationRequirement = peFile.RequestedElevation,
            ProductCode = manifest.RegistrationId,
        };

        if (manifest.RegistersArpEntry)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = manifest.DisplayName,
                    Publisher = manifest.Publisher,
                    DisplayVersion = manifest.DisplayVersion ?? manifest.RegistrationVersion,
                    ProductCode = manifest.RegistrationId,
                    UpgradeCode = manifest.UpgradeCode,
                },
            ];
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Burn,
            Installers = [installer],
            ProductName = manifest.DisplayName ?? version.ProductName,
            Publisher = manifest.Publisher ?? version.CompanyName,
            ProductVersion = manifest.DisplayVersion ?? manifest.RegistrationVersion ?? version.ProductVersion,
            Copyright = version.LegalCopyright,
        };
    }

    /// <summary>
    /// Walks the PE section table on the raw stream (DOS header → COFF header → section
    /// headers) and returns the raw data of the <c>.wixburn</c> section, or null when the
    /// image has no such section. Structural shortfalls also yield null: the PE was already
    /// validated by <see cref="PeFile"/>, so anything unreadable here is simply not a bundle.
    /// </summary>
    private static byte[]? ReadWixburnSection(Stream stream)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        if (!TryReadAt(stream, 0, dosHeader) || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return null;
        }

        // COFF header after the "PE\0\0" signature: NumberOfSections at offset 2,
        // SizeOfOptionalHeader at offset 16; the section table follows the optional header.
        uint peHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader[60..]);
        Span<byte> coffHeader = stackalloc byte[24];
        if (!TryReadAt(stream, peHeaderOffset, coffHeader)
            || BinaryPrimitives.ReadUInt32LittleEndian(coffHeader) != 0x00004550)
        {
            return null;
        }

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[6..]);
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[20..]);
        byte[] table = new byte[sectionCount * SectionHeaderSize];
        if (!TryReadAt(stream, peHeaderOffset + 24 + (uint)optionalHeaderSize, table))
        {
            return null;
        }

        for (int i = 0; i < sectionCount; i++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(i * SectionHeaderSize, SectionHeaderSize);
            if (!entry[..8].SequenceEqual(WixburnSectionName))
            {
                continue;
            }

            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
            long available = Math.Min(rawSize, stream.Length - rawPointer);
            if (available < 4)
            {
                return null;
            }

            byte[] section = new byte[available];
            return TryReadAt(stream, rawPointer, section) ? section : null;
        }

        return null;
    }

    /// <summary>Reads the UX container: container 0, attached at the header's stub size.</summary>
    /// <exception cref="InvalidDataException">The container extends past the end of the file.</exception>
    private static byte[] ReadUxContainer(Stream stream, BurnSectionHeader header)
    {
        uint size = header.ContainerSizes[0];
        if (size == 0 || header.StubSize + (long)size > stream.Length)
        {
            throw new InvalidDataException("The Burn UX container extends past the end of the file or is empty.");
        }

        byte[] container = new byte[size];
        stream.Position = header.StubSize;
        stream.ReadExactly(container);
        return container;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }
}
