using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Builds Squirrel.Windows / Clowd.Squirrel setup executables for probe tests: a PE stub
/// with a zip overlay that either wraps the release <c>.nupkg</c> (classic Squirrel
/// <c>Setup.exe</c>) or is the nupkg itself (Clowd.Squirrel). Zips are hand-written with
/// stored (uncompressed) entries so tests fully control declared sizes for hostile-input
/// scenarios.
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

    /// <summary>Builds a classic Squirrel Setup.exe: stub + zip overlay wrapping the nupkg.</summary>
    public static byte[] BuildClassicSetup(
        byte[] nupkg,
        string nupkgName = "Contoso.Chat-1.2.3-full.nupkg",
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null,
        long? nupkgDeclaredSize = null)
        => AdvancedInstallerFixtures.Concat(
            PeFixtures.BuildExe(machine, version, manifestXml),
            BuildStoredZip([("RELEASES", Encoding.UTF8.GetBytes("stub-releases-index")), (nupkgName, nupkg)], declaredSizeOverrideForLastEntry: nupkgDeclaredSize));

    /// <summary>Builds a Clowd.Squirrel-style setup: stub + the nupkg appended directly as overlay.</summary>
    public static byte[] BuildClowdSetup(
        byte[] nupkg,
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null)
        => AdvancedInstallerFixtures.Concat(PeFixtures.BuildExe(machine, version, manifestXml), nupkg);

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
}
