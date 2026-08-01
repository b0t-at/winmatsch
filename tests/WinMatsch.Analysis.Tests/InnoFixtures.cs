using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using SharpCompress.Compressors.LZMA;
using WinMatsch.Analysis.Inno;

namespace WinMatsch.Analysis.Tests;

internal static class InnoFixtures
{
    public sealed record Language(string Name, uint LanguageId, uint CodePage = 1252);

    public sealed class Options
    {
        public Version Version { get; set; } = new(6, 4, 0, 1);

        public bool Unicode { get; set; } = true;

        public string AppName { get; set; } = "Contoso Commander";

        public byte[]? AppNameBytesOverride { get; set; }

        public string AppVerName { get; set; } = "Contoso Commander 2.5";

        public string AppId { get; set; } = "{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}";

        public string AppVersion { get; set; } = "2.5.0";

        public string Publisher { get; set; } = "Contoso Ltd";

        public string DefaultDirName { get; set; } = @"{autopf}\Contoso Commander";

        public string UninstallDisplayName { get; set; } = "Contoso Commander 2.5";

        public string CreateUninstallRegKey { get; set; } = "yes";

        public string Uninstallable { get; set; } = "yes";

        public string ArchitecturesAllowed { get; set; } = "x64compatible";

        public string ArchitecturesInstallIn64BitMode { get; set; } = "x64compatible";

        public byte OldArchitecturesAllowed { get; set; } = 0x02;

        public byte OldArchitecturesInstallIn64BitMode { get; set; }

        public InnoPrivilegeLevel PrivilegesRequired { get; set; } = InnoPrivilegeLevel.Admin;

        public byte PrivilegeOverrides { get; set; }

        public List<Language> Languages { get; set; } = [new("english", 1033)];

        public int? AnsiEncodingCodePageOverride { get; set; }

        public List<Machine> PayloadMachines { get; set; } = [];

        public byte[] AdditionalPayloadBytes { get; set; } = [];

        public byte HeaderCompression { get; set; }

        public byte[] CompiledCode { get; set; } = [];

        public bool CorruptLoaderChecksum { get; set; }

        public uint LoaderRevision { get; set; } = 1;

        public bool CorruptHeaderChecksum { get; set; }

        public uint? FirstStringLengthOverride { get; set; }

        public uint? StoredHeaderSizeOverride { get; set; }

        public bool CompressHeader { get; set; }

        public uint? LzmaDictionarySizeOverride { get; set; }

        public bool WriteLegacyLoaderPointer { get; set; } = true;
    }

    public static byte[] BuildInstaller(Options? options = null)
    {
        options ??= new Options();
        byte[] mainHeader = BuildMainHeader(options);
        byte[] packedHeader = options.CompressHeader
            ? CompressLzma(mainHeader, options.LzmaDictionarySizeOverride)
            : mainHeader;
        byte[] framed = FrameChunks(packedHeader);
        byte[] blockHeader = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(
            blockHeader.AsSpan(4),
            options.StoredHeaderSizeOverride ?? (uint)framed.Length);
        blockHeader[8] = options.CompressHeader ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            blockHeader,
            FixtureCrc32(blockHeader.AsSpan(4, 5)));
        if (options.CorruptHeaderChecksum)
        {
            blockHeader[0] ^= 0x5A;
        }

        byte[] setupVersion = new byte[64];
        string versionText = $"Inno Setup Setup Data ({options.Version}){(options.Unicode ? " (u)" : "")}";
        Encoding.ASCII.GetBytes(versionText).CopyTo(setupVersion, 0);
        byte[] setupHeader = [.. setupVersion, .. blockHeader, .. framed];

        byte[] stub = PeFixtures.BuildExe(Machine.I386);
        int tableOffset = stub.Length;
        int setupHeaderOffset = tableOffset + 44;
        int dataOffset = setupHeaderOffset + setupHeader.Length;
        byte[] table = BuildLoaderTable(
            setupHeaderOffset,
            dataOffset,
            options.LoaderRevision,
            options.CorruptLoaderChecksum);

        List<byte> payload = [];
        foreach (Machine machine in options.PayloadMachines)
        {
            payload.AddRange(PeFixtures.BuildExe(machine));
            payload.AddRange(new byte[17]);
        }

        payload.AddRange(options.AdditionalPayloadBytes);
        byte[] installer = [.. stub, .. table, .. setupHeader, .. payload];
        if (options.WriteLegacyLoaderPointer)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(installer.AsSpan(0x30), 0x6F6E6E49);
            BinaryPrimitives.WriteUInt32LittleEndian(installer.AsSpan(0x34), (uint)tableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(installer.AsSpan(0x38), ~(uint)tableOffset);
        }

        return installer;
    }

    public static byte[] BuildMarkerPayload(int invalidMarkers, byte[] finalPayload)
    {
        List<byte> result = [];
        for (int i = 0; i < invalidMarkers; i++)
        {
            result.AddRange("zlb\x1a"u8.ToArray());
            result.AddRange([0xFF, 0xFF, 0xFF, (byte)i]);
        }

        result.AddRange("zlb\x1a"u8.ToArray());
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(finalPayload);
        }

        result.AddRange(compressed.ToArray());
        return [.. result];
    }

    public static byte[] BuildPseudoPe(Machine machine)
    {
        byte[] image = new byte[128];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 64);
        "PE\0\0"u8.CopyTo(image.AsSpan(64));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(68), (ushort)machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(70), 0); // No sections: not a valid image.
        return image;
    }

    private static byte[] BuildMainHeader(Options options)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = options.Unicode
            ? Encoding.Unicode
            : Encoding.GetEncoding(
                options.AnsiEncodingCodePageOverride
                    ?? checked((int)(options.Languages.FirstOrDefault()?.CodePage ?? 1252)),
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ExceptionFallback);
        List<byte> bytes = [];

        AddString(options.AppName, rawBytes: options.AppNameBytesOverride);
        AddString(options.AppVerName);
        AddString(options.AppId);
        AddString("© Contoso");
        AddString(options.Publisher);
        AddString("https://contoso.example");
        AddString("+1 555 0100");
        AddString("https://contoso.example/support");
        AddString("https://contoso.example/update");
        AddString(options.AppVersion);
        AddString(options.DefaultDirName);
        AddString("Contoso Commander");
        AddString("setup");
        AddString("{app}");
        AddString(options.UninstallDisplayName);
        AddString("{app}\\unins000.exe");
        AddString("ContosoMutex");
        AddString("");
        AddString("");
        AddString("");
        AddString("");
        AddString("");
        AddString("");
        AddString("");
        AddString(options.CreateUninstallRegKey);
        AddString(options.Uninstallable);
        AddString("*.exe");
        AddString("SetupMutex");
        if (options.Version >= new Version(5, 6, 1))
        {
            AddString("no");
            AddString("no");
        }

        if (options.Version >= new Version(6, 3, 0))
        {
            AddString(options.ArchitecturesAllowed);
            AddString(options.ArchitecturesInstallIn64BitMode);
        }

        AddString("", forceAnsi: true);
        AddString("", forceAnsi: true);
        AddString("", forceAnsi: true);
        AddBlob(options.CompiledCode);

        if (!options.Unicode)
        {
            bytes.AddRange(new byte[32]);
        }

        AddUInt32((uint)options.Languages.Count);
        for (int i = 0; i < 15; i++)
        {
            AddUInt32(0);
        }

        bytes.AddRange(new byte[20]);
        if (options.Version < new Version(6, 4, 0, 1))
        {
            bytes.AddRange(new byte[8]);
        }

        if (options.Version >= new Version(6, 0, 0))
        {
            bytes.AddRange(new byte[9]);
        }

        bytes.Add(0); // WizardImageAlphaFormat
        bytes.AddRange(new byte[options.Version >= new Version(6, 4, 0) ? 48 : 28]);
        bytes.AddRange(new byte[12]);
        bytes.Add(1); // UninstallLogMode
        bytes.Add(0); // DirExistsWarning
        bytes.Add((byte)options.PrivilegesRequired);
        if (options.Version >= new Version(5, 7, 0))
        {
            bytes.Add(options.PrivilegeOverrides);
        }

        bytes.Add(0); // ShowLanguageDialog
        bytes.Add(0); // LanguageDetectionMethod
        bytes.Add(options.HeaderCompression);
        if (options.Version < new Version(6, 3, 0))
        {
            bytes.Add(options.OldArchitecturesAllowed);
            bytes.Add(options.OldArchitecturesInstallIn64BitMode);
        }

        bytes.AddRange(new byte[2]);
        bytes.AddRange(new byte[8]);
        bytes.AddRange(new byte[FixtureSetupFlagByteCount(options.Version)]);

        foreach (Language language in options.Languages)
        {
            AddString(language.Name);
            AddString(language.Name);
            AddString("Segoe UI");
            AddString("Segoe UI");
            AddString("Segoe UI");
            AddString("Segoe UI");
            AddString("");
            AddString("");
            AddString("");
            AddString("");
            AddUInt32(language.LanguageId);
            if (!options.Unicode)
            {
                AddUInt32(language.CodePage);
            }

            bytes.AddRange(new byte[16]);
            bytes.Add(0);
        }

        if (options.FirstStringLengthOverride is { } firstStringLength)
        {
            byte[] result = [.. bytes];
            BinaryPrimitives.WriteUInt32LittleEndian(result, firstStringLength);
            return result;
        }

        return [.. bytes];

        void AddString(string value, bool forceAnsi = false, byte[]? rawBytes = null)
        {
            byte[] encoded = rawBytes ?? (forceAnsi ? Encoding.GetEncoding(1252) : encoding).GetBytes(value);
            AddUInt32((uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        void AddBlob(byte[] value)
        {
            AddUInt32((uint)value.Length);
            bytes.AddRange(value);
        }

        void AddUInt32(uint value)
        {
            byte[] encoded = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
            bytes.AddRange(encoded);
        }
    }

    private static byte[] BuildLoaderTable(
        int headerOffset,
        int dataOffset,
        uint revision,
        bool corrupt)
    {
        byte[] table = new byte[44];
        byte[] magic = [0x72, 0x44, 0x6C, 0x50, 0x74, 0x53, 0xCD, 0xE6, 0xD7, 0x7B, 0x0B, 0x2A];
        magic.CopyTo(table, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), revision);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(32), (uint)headerOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(36), (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(40), FixtureCrc32(table.AsSpan(0, 40)));
        if (corrupt)
        {
            table[40] ^= 0x80;
        }

        return table;
    }

    private static byte[] FrameChunks(byte[] data)
    {
        List<byte> framed = [];
        for (int offset = 0; offset < data.Length; offset += 4096)
        {
            int length = Math.Min(4096, data.Length - offset);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, FixtureCrc32(data.AsSpan(offset, length)));
            framed.AddRange(crc);
            framed.AddRange(data.AsSpan(offset, length).ToArray());
        }

        return [.. framed];
    }

    private static byte[] CompressLzma(byte[] data, uint? dictionarySizeOverride)
    {
        using var output = new MemoryStream();
        byte[] properties;
        using (var lzma = new LzmaStream(new LzmaEncoderProperties(eos: true), false, output))
        {
            properties = lzma.Properties;
            lzma.Write(data);
        }

        if (dictionarySizeOverride is { } dictionarySize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), dictionarySize);
        }

        return [.. properties, .. output.ToArray()];
    }

    private static int FixtureSetupFlagByteCount(Version version)
        => version switch
        {
            { Major: 5, Minor: 6, Build: 0 } => 6,
            { Major: 6, Minor: >= 4 } => 6,
            _ => throw new NotSupportedException($"No independent fixture layout is defined for setup data {version}."),
        };

    private static uint FixtureCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint lowBitMask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320u & lowBitMask);
            }
        }

        return ~crc;
    }

}
