using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>Builds in-memory MSIX/AppX packages and bundles (plain zips) for tests.</summary>
internal static class MsixFixtures
{
    /// <summary>The publisher subject whose publisher id is the well-known <c>8wekyb3d8bbwe</c>.</summary>
    public const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>Builds a package zip with an <c>AppxManifest.xml</c> and an optional signature entry.</summary>
    public static MemoryStream BuildPackage(
        string manifestXml,
        byte[]? signature = null,
        params (string Name, byte[]? Content)[] additionalEntries)
        => BuildZip(
            [("AppxManifest.xml", Encoding.UTF8.GetBytes(manifestXml)), ("AppxSignature.p7x", signature), .. additionalEntries]);

    /// <summary>Builds a bundle zip with an <c>AppxMetadata/AppxBundleManifest.xml</c> and an optional signature entry.</summary>
    public static MemoryStream BuildBundle(string bundleManifestXml, byte[]? signature = null)
        => BuildZip(
            ("AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes(bundleManifestXml)),
            ("AppxSignature.p7x", signature));

    /// <summary>Composes a package manifest; null sections are omitted.</summary>
    public static string PackageManifest(
        string identityName = "Contoso.Editor",
        string publisher = "CN=Contoso Ltd, O=Contoso Ltd, C=US",
        string version = "2.4.1.0",
        string? processorArchitecture = "x64",
        string? displayName = "Contoso Editor",
        string? publisherDisplayName = "Contoso Ltd",
        string? dependencies = """<TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />""",
        string? capabilities = null)
    {
        string architectureAttribute = processorArchitecture is null
            ? ""
            : $" ProcessorArchitecture=\"{processorArchitecture}\"";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
              <Identity Name="{identityName}" Publisher="{publisher}" Version="{version}"{architectureAttribute} />
              <Properties>
                <DisplayName>{displayName}</DisplayName>
                <PublisherDisplayName>{publisherDisplayName}</PublisherDisplayName>
              </Properties>
              <Dependencies>
                {dependencies}
              </Dependencies>
              {(capabilities is null ? "" : $"<Capabilities>{capabilities}</Capabilities>")}
            </Package>
            """;
    }

    public static MemoryStream MarkZipEntryEncrypted(MemoryStream source, string entryName)
    {
        byte[] bytes = source.ToArray();
        Span<byte> data = bytes;
        int centralOffset = data.IndexOf("PK\x01\x02"u8);
        while (centralOffset >= 0)
        {
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(centralOffset + 28)..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(centralOffset + 30)..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(centralOffset + 32)..]);
            string name = Encoding.UTF8.GetString(data.Slice(centralOffset + 46, nameLength));
            if (string.Equals(name, entryName, StringComparison.Ordinal))
            {
                int localOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(centralOffset + 42)..]));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    data[(localOffset + 6)..],
                    (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[(localOffset + 6)..]) | 1));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    data[(centralOffset + 8)..],
                    (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[(centralOffset + 8)..]) | 1));
                return new MemoryStream(bytes, writable: false);
            }

            centralOffset += 46 + nameLength + extraLength + commentLength;
            if (centralOffset >= data.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(data[centralOffset..]) != 0x02014B50u)
            {
                break;
            }
        }

        throw new InvalidDataException($"Synthetic ZIP has no '{entryName}' entry.");
    }

    private static MemoryStream BuildZip(params (string Name, byte[]? Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[]? content) in entries)
            {
                if (content is null)
                {
                    continue;
                }

                ZipArchiveEntry entry = archive.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using Stream entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        NormalizeZipMetadata(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        stream.Position = 0;
        return stream;
    }

    internal static void NormalizeZipMetadata(Span<byte> bytes)
    {
        ReadOnlySpan<byte> endSignature = [0x50, 0x4B, 0x05, 0x06];
        int searchStart = Math.Max(0, bytes.Length - (ushort.MaxValue + 22));
        int relativeEndOffset = bytes.Slice(searchStart).LastIndexOf(endSignature);
        if (relativeEndOffset < 0)
        {
            throw new InvalidDataException("Synthetic ZIP has no end-of-central-directory record.");
        }

        int endOffset = searchStart + relativeEndOffset;
        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(endOffset + 10, 2));
        int centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(endOffset + 16, 4)));
        for (int index = 0; index < entryCount; index++)
        {
            Span<byte> header = bytes.Slice(centralOffset);
            if (header.Length < 46
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50)
            {
                throw new InvalidDataException("Synthetic ZIP has an invalid central-directory entry.");
            }

            header[5] = 0;
            header.Slice(38, 4).Clear();
            int fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(30, 2));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(32, 2));
            centralOffset = checked(centralOffset + 46 + fileNameLength + extraLength + commentLength);
        }
    }
}
