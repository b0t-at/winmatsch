using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

/// <summary>Independently encodes the ADVINSTSFX footer and file-table layout.</summary>
internal static class AdvancedInstallerFixtures
{
    public static VersionStrings BrandedStub { get; } = new(
        ProductName: "Contoso Studio",
        CompanyName: "Contoso Ltd",
        ProductVersion: "3.1.0",
        FileDescription: "This installation was built with Advanced Installer");

    public static byte[] BuildInstaller(
        (string Name, string Value)[] msiProperties,
        string? template = "x64;1033",
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null,
        string msiEntryName = "payload.bin",
        string? creatingApplication = "WiX Toolset v4",
        bool nestedSevenZip = false,
        bool xorPayload = false)
    {
        byte[] msi = MsiFixtures.BuildMsi(msiProperties, template, creatingApplication);
        FixtureEntry entry = nestedSevenZip
            ? new(3, 7, xorPayload ? 2u : 0u, "container.dat", SevenZipFixtures.Build(("product.msi", msi)))
            : new(1, 0, xorPayload ? 2u : 0u, msiEntryName, msi);
        return BuildContainer([entry], machine, version, manifestXml);
    }

    public static byte[] BuildContainer(
        FixtureEntry[] entries,
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null)
    {
        byte[] stub = PeFixtures.BuildExe(machine, version, manifestXml);
        using var output = new MemoryStream();
        output.Write(stub);

        uint fileDataStart = checked((uint)output.Position);
        List<(FixtureEntry Entry, uint Offset)> located = [];
        foreach (FixtureEntry entry in entries)
        {
            uint offset = checked((uint)output.Position);
            byte[] stored = [.. entry.Data];
            if (entry.XorFlag == 2)
            {
                for (int i = 0; i < Math.Min(stored.Length, 0x200); i++)
                {
                    stored[i] ^= 0xFF;
                }
            }

            output.Write(stored);
            located.Add((entry, offset));
        }

        uint tablePointer = checked((uint)output.Position);
        foreach ((FixtureEntry entry, uint offset) in located)
        {
            byte[] name = Encoding.Unicode.GetBytes(entry.Name);
            WriteUInt32(output, entry.Type0);
            WriteUInt32(output, entry.Type1);
            WriteUInt32(output, entry.XorFlag);
            WriteUInt32(output, checked((uint)entry.Data.Length));
            WriteUInt32(output, offset);
            WriteUInt32(output, checked((uint)entry.Name.Length));
            output.Write(name);
        }

        uint footerOffset = checked((uint)output.Position);
        WriteUInt32(output, 0);
        WriteUInt32(output, footerOffset);
        WriteUInt32(output, checked((uint)entries.Length));
        WriteUInt32(output, 100);
        WriteUInt32(output, tablePointer);
        WriteUInt32(output, tablePointer);
        WriteUInt32(output, fileDataStart);
        output.Write(Encoding.ASCII.GetBytes("0123456789ABCDEF0123456789ABCDEF"));
        WriteUInt32(output, 0x32);
        output.Write("ADVINSTSFX"u8);
        return output.ToArray();
    }

    public static byte[] BuildRawOverlay(byte[] overlay, VersionStrings? version = null)
        => Concat(PeFixtures.BuildExe(version: version), overlay);

    public static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(static part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    public sealed record FixtureEntry(
        uint Type0,
        uint Type1,
        uint XorFlag,
        string Name,
        byte[] Data);

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
    }
}
