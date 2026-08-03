using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Xml;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Pe;

/// <summary>
/// A parsed Portable Executable: the machine-derived architecture, the DLL flag, the strings
/// from the <c>VS_VERSIONINFO</c> resource, and the requested execution level from the
/// embedded application manifest. The stream must be seekable and positioned at the start of
/// the image; it is left open and is not disposed with this instance.
/// </summary>
public sealed class PeFile : IDisposable
{
    private const int RtVersion = 16;
    private const int RtManifest = 24;
    private const uint SubdirectoryFlag = 0x80000000;

    private readonly PEReader _reader;

    /// <summary>Loads the PE headers and resources from the stream.</summary>
    /// <exception cref="BadImageFormatException">The stream does not contain a valid PE image.</exception>
    public PeFile(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        try
        {
            PEHeaders headers = _reader.PEHeaders;
            Architecture = MapMachine(headers.CoffHeader.Machine);
            IsDll = headers.IsDll;

            byte[]? versionData = ReadFirstResourceData(RtVersion);
            VersionInfo = versionData is null ? new VersionInfo() : VersionInfo.Parse(versionData);

            byte[]? manifestData = ReadFirstResourceData(RtManifest);
            RequestedElevation = manifestData is null ? null : ParseRequestedElevation(manifestData);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The architecture derived from the COFF machine field. Unknown machines fall back to
    /// <see cref="Architecture.X86"/>, the most permissive choice: an x86 manifest entry runs
    /// everywhere via emulation.
    /// </summary>
    public Architecture Architecture { get; }

    /// <summary>Whether the image is a DLL rather than an executable.</summary>
    public bool IsDll { get; }

    /// <summary>The strings of the first version-resource string table; all null when absent.</summary>
    public VersionInfo VersionInfo { get; }

    /// <summary>
    /// The elevation requirement declared by the embedded application manifest:
    /// <c>requireAdministrator</c> maps to <see cref="ElevationRequirement.ElevationRequired"/>;
    /// <c>highestAvailable</c>, <c>asInvoker</c> and a missing manifest map to null.
    /// </summary>
    public ElevationRequirement? RequestedElevation { get; }

    /// <summary>
    /// The installation scope hinted at by the manifest: an installer that requires
    /// administrator rights most likely installs machine-wide. Null when no such hint exists.
    /// </summary>
    public Scope? ScopeHint
        => RequestedElevation == ElevationRequirement.ElevationRequired ? Scope.Machine : null;

    public void Dispose() => _reader.Dispose();

    private static Architecture MapMachine(Machine machine) => machine switch
    {
        Machine.Amd64 => Architecture.X64,
        Machine.I386 => Architecture.X86,
        Machine.Arm64 => Architecture.Arm64,
        Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => Architecture.Arm,
        _ => Architecture.X86,
    };

    /// <summary>
    /// Reads the data of the first resource of the given type: root directory → type entry →
    /// first name → first language → data entry. Returns null when the image has no resource
    /// section, the type is absent, or the directory tree is malformed.
    /// </summary>
    private byte[]? ReadFirstResourceData(int resourceTypeId)
    {
        PEHeader? peHeader = _reader.PEHeaders.PEHeader;
        if (peHeader is null)
        {
            return null;
        }

        DirectoryEntry directory = peHeader.ResourceTableDirectory;
        if (directory.RelativeVirtualAddress == 0
            || directory.Size <= 0
            || directory.Size > AnalysisLimits.MaxResourceBytes)
        {
            return null;
        }

        PEMemoryBlock block = _reader.GetSectionData(directory.RelativeVirtualAddress);
        if (block.Length < directory.Size)
        {
            return null;
        }

        ReadOnlySpan<byte> resources = block.GetContent(0, directory.Size).AsSpan();

        (int Offset, bool IsSubdirectory)? typeEntry = FindIdEntry(resources, 0, resourceTypeId);
        if (typeEntry is not { IsSubdirectory: true })
        {
            return null;
        }

        (int Offset, bool IsSubdirectory)? nameEntry = FirstEntry(resources, typeEntry.Value.Offset);
        if (nameEntry is not { IsSubdirectory: true })
        {
            return null;
        }

        (int Offset, bool IsSubdirectory)? languageEntry = FirstEntry(resources, nameEntry.Value.Offset);
        if (languageEntry is not { IsSubdirectory: false })
        {
            return null;
        }

        // IMAGE_RESOURCE_DATA_ENTRY: data RVA, size, code page, reserved. Unlike the directory
        // offsets above (relative to the start of the resource data), the data pointer is an RVA.
        int dataEntryOffset = languageEntry.Value.Offset;
        if (dataEntryOffset < 0 || dataEntryOffset + 16 > resources.Length)
        {
            return null;
        }

        uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(resources[dataEntryOffset..]);
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(resources[(dataEntryOffset + 4)..]);
        long dataOffset = dataRva - (long)(uint)directory.RelativeVirtualAddress;
        if (dataSize == 0
            || dataSize > AnalysisLimits.MaxResourceBytes
            || dataOffset < 0
            || dataOffset + dataSize > resources.Length)
        {
            return null;
        }

        return resources.Slice((int)dataOffset, (int)dataSize).ToArray();
    }

    /// <summary>Finds the ID entry with the given identifier in the directory at <paramref name="directoryOffset"/>.</summary>
    private static (int Offset, bool IsSubdirectory)? FindIdEntry(ReadOnlySpan<byte> resources, int directoryOffset, int id)
    {
        if (directoryOffset < 0 || directoryOffset + 16 > resources.Length)
        {
            return null;
        }

        // ID entries follow the named entries in IMAGE_RESOURCE_DIRECTORY.
        int namedCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 12)..]);
        int idCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 14)..]);
        for (int i = namedCount; i < namedCount + idCount; i++)
        {
            int entryOffset = directoryOffset + 16 + (i * 8);
            if (entryOffset + 8 > resources.Length)
            {
                return null;
            }

            uint entryId = BinaryPrimitives.ReadUInt32LittleEndian(resources[entryOffset..]);
            if (entryId == (uint)id)
            {
                return ReadEntryTarget(resources, entryOffset);
            }
        }

        return null;
    }

    /// <summary>Returns the first entry of the directory at <paramref name="directoryOffset"/>, named or not.</summary>
    private static (int Offset, bool IsSubdirectory)? FirstEntry(ReadOnlySpan<byte> resources, int directoryOffset)
    {
        if (directoryOffset < 0 || directoryOffset + 24 > resources.Length)
        {
            return null;
        }

        int namedCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 12)..]);
        int idCount = BinaryPrimitives.ReadUInt16LittleEndian(resources[(directoryOffset + 14)..]);
        if (namedCount + idCount == 0)
        {
            return null;
        }

        return ReadEntryTarget(resources, directoryOffset + 16);
    }

    private static (int Offset, bool IsSubdirectory) ReadEntryTarget(ReadOnlySpan<byte> resources, int entryOffset)
    {
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(resources[(entryOffset + 4)..]);
        return ((int)(raw & ~SubdirectoryFlag), (raw & SubdirectoryFlag) != 0);
    }

    /// <summary>
    /// Extracts the <c>requestedExecutionLevel</c> level from an application manifest.
    /// Malformed manifests are common in the wild and are treated as "no information".
    /// </summary>
    private static ElevationRequirement? ParseRequestedElevation(byte[] manifestUtf8)
    {
        try
        {
            using var manifestStream = new MemoryStream(manifestUtf8);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(manifestStream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, "requestedExecutionLevel", StringComparison.OrdinalIgnoreCase))
                {
                    string? level = reader.GetAttribute("level");
                    return string.Equals(level, "requireAdministrator", StringComparison.OrdinalIgnoreCase)
                        ? ElevationRequirement.ElevationRequired
                        : null;
                }
            }
        }
        catch (XmlException)
        {
            // Not well-formed XML: treat as if the manifest carried no elevation information.
        }

        return null;
    }
}
