using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace WinMatsch.Analysis.Tests;

/// <summary>How <see cref="NsisFixtures"/> stores the installer header in the archive.</summary>
public enum NsisCompressor
{
    StoredNonSolid,
    DeflateNonSolid,
    DeflateSolid,
    LzmaNonSolid,
    LzmaSolid,
    Bzip2Solid,
    Bzip2NonSolid,
    LzmaBcjSolid,
    CorruptDeflateNonSolid,
    CorruptLzmaSolid,
    NoDataAtAll,
}

/// <summary>
/// Builds small but structurally real NSIS 3 installers for tests: a PE stub via
/// <see cref="PeFixtures"/> plus the archive in the overlay — the 28-byte first header
/// (0xDEADBEEF + "NullsoftInst") followed by the compressed installer header with block
/// headers, an encoded strings block, a language table and WriteRegStr/SetFlag instructions.
/// Everything is written by the book from NSIS's <c>Source/exehead/fileform.h</c>
/// independently from the production parser, so the fixtures double as a cross-check — same
/// as <see cref="BurnFixtures"/> does for the Burn reader.
/// </summary>
internal static class NsisFixtures
{
    public const string DefaultDisplayName = "Contoso App";
    public const string DefaultDisplayVersion = "2.5.0";
    public const string DefaultPublisher = "Contoso Ltd";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Contoso App";

    public const int HklmRoot = unchecked((int)0x80000002);
    public const int ShctxRoot = 0; // SHCTX: the stub resolves HKLM/HKCU from the shell var context.

    private const int FixedPartSize = 300;
    private const int EntrySize = 28;
    private const int RegSz = 1;
    private const int EwNop = 2;
    private const int EwSetFlag = 13;
    private const int EwWriteReg = 51;

    /// <summary>What the fixture script does; the defaults model a typical x64 installer.</summary>
    public sealed class Options
    {
        public bool Unicode { get; set; } = true;

        public NsisCompressor Compressor { get; set; } = NsisCompressor.DeflateNonSolid;

        public ushort Lcid { get; set; } = 1033;

        /// <summary>The string the language table's LANG_NAME slot points at ($(^Name)).</summary>
        public string LangName { get; set; } = DefaultDisplayName;

        /// <summary>The default $INSTDIR; null leaves install_directory_ptr at 0 (unset).</summary>
        public IReadOnlyList<Token>? InstallDirectory { get; set; } =
            [Token.ShellProgramFiles(x64: true), Token.Lit(@"\" + DefaultDisplayName)];

        public List<RegWrite> RegistryWrites { get; set; } =
        [
            new RegWrite(HklmRoot, [Token.Lit(UninstallKey)], "DisplayName", [Token.Lit(DefaultDisplayName)]),
            new RegWrite(HklmRoot, [Token.Lit(UninstallKey)], "DisplayVersion", [Token.Lit(DefaultDisplayVersion)]),
            new RegWrite(HklmRoot, [Token.Lit(UninstallKey)], "Publisher", [Token.Lit(DefaultPublisher)]),
            new RegWrite(HklmRoot, [Token.Lit(UninstallKey)], "UninstallString", [Token.Var(21), Token.Lit(@"\uninstall.exe")]),
        ];

        public bool SetRegView64 { get; set; }

        /// <summary>Overrides the first header's length_of_header for corruption tests.</summary>
        public int? DeclaredHeaderSizeOverride { get; set; }

        /// <summary>Extra zero bytes before the first header, to exercise the 512-byte scan.</summary>
        public int FirstHeaderPadding { get; set; }

        public VersionStrings? Version { get; set; }

        public string? ManifestXml { get; set; }
    }

    /// <summary>One WriteReg instruction; type REG_SZ is what WriteRegStr compiles to.</summary>
    public sealed record RegWrite(int Root, IReadOnlyList<Token> Key, string ValueName, IReadOnlyList<Token> Value, int Type = RegSz);

    /// <summary>Builds the complete installer image: stub PE, padding, first header, archive.</summary>
    public static byte[] BuildInstaller(Options? options = null)
    {
        options ??= new Options();
        byte[] header = BuildHeader(options);
        byte[] archive = WrapArchive(header, options.Compressor, options.DeclaredHeaderSizeOverride);

        byte[] stub = PeFixtures.BuildExe(Machine.I386, options.Version, options.ManifestXml);
        int padding = ((stub.Length + 511) / 512 * 512) - stub.Length + options.FirstHeaderPadding;
        return [.. stub, .. new byte[padding], .. archive];
    }

    /// <summary>
    /// The installer header: the 300-byte NSIS 3 fixed part (flags, eight block headers,
    /// langtable_size at 100, install_directory_ptr at 280) followed by the entries, strings
    /// and langtables blocks.
    /// </summary>
    private static byte[] BuildHeader(Options options)
    {
        var strings = new StringsBlock(options.Unicode);

        int installDirectoryPtr = options.InstallDirectory is null ? 0 : strings.Add(options.InstallDirectory);
        int langNamePtr = strings.Add([Token.Lit(options.LangName)]);

        // Entries: some noise, the registry writes, optionally SetRegView 64
        // (EW_SETFLAG on exec flag 12, alter_reg_view, with the value string "256").
        List<int[]> entries = [[EwNop, 0, 0, 0, 0, 0, 0]];
        foreach (RegWrite write in options.RegistryWrites)
        {
            entries.Add(
            [
                EwWriteReg,
                write.Root,
                strings.Add(write.Key),
                strings.Add([Token.Lit(write.ValueName)]),
                strings.Add(write.Value),
                write.Type,
                RegSz,
            ]);
        }

        if (options.SetRegView64)
        {
            entries.Add([EwSetFlag, 12, strings.Add([Token.Lit("256")]), 0, 0, 0, 0]);
        }

        byte[] stringsBytes = strings.ToArray();

        // Langtable: LANGID, dialog offset, RTL flag, then the language strings —
        // branding, caption and name per exehead/lang.h (LANG_NAME is index 2).
        int[] langStrings = [0, 0, langNamePtr];
        byte[] langTable = new byte[10 + (langStrings.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(langTable, options.Lcid);
        for (int i = 0; i < langStrings.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(langTable.AsSpan(10 + (i * 4)), langStrings[i]);
        }

        int entriesOffset = FixedPartSize;
        int stringsOffset = entriesOffset + (entries.Count * EntrySize);
        int langTablesOffset = stringsOffset + stringsBytes.Length;
        int end = langTablesOffset + langTable.Length;

        byte[] header = new byte[end];
        WriteBlockHeader(header, 0, entriesOffset, 0);                 // Pages.
        WriteBlockHeader(header, 1, entriesOffset, 0);                 // Sections.
        WriteBlockHeader(header, 2, entriesOffset, entries.Count);     // Entries.
        WriteBlockHeader(header, 3, stringsOffset, 0);                 // Strings (count unused).
        WriteBlockHeader(header, 4, langTablesOffset, 1);              // Langtables.
        WriteBlockHeader(header, 5, end, 0);                           // Ctlcolors.
        WriteBlockHeader(header, 6, end, 0);                           // Bgfont.
        WriteBlockHeader(header, 7, end, 0);                           // Data.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(100), langTable.Length); // langtable_size.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(280), installDirectoryPtr);

        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = 0; j < 7; j++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(entriesOffset + (i * EntrySize) + (j * 4)), entries[i][j]);
            }
        }

        stringsBytes.CopyTo(header, stringsOffset);
        langTable.CopyTo(header, langTablesOffset);
        return header;
    }

    private static void WriteBlockHeader(byte[] header, int index, int offset, int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + (index * 8)), offset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + (index * 8) + 4), count);
    }

    /// <summary>
    /// The first header plus the archive data in the requested storage mode. Non-solid data
    /// blocks carry a uint32 size prefix whose high bit marks compression; solid data is one
    /// continuous compressed stream starting with the installer header.
    /// </summary>
    private static byte[] WrapArchive(byte[] header, NsisCompressor compressor, int? declaredSizeOverride)
    {
        int declaredSize = declaredSizeOverride ?? header.Length;
        byte[] data = compressor switch
        {
            NsisCompressor.StoredNonSolid => [.. SizePrefix((uint)declaredSize), .. header],
            NsisCompressor.DeflateNonSolid => CompressedBlock(Deflate(header)),
            NsisCompressor.DeflateSolid => Deflate(header),
            NsisCompressor.LzmaNonSolid => CompressedBlock(Lzma(header)),
            NsisCompressor.LzmaSolid => Lzma(header),
            NsisCompressor.Bzip2Solid => [0x31, 0x05, 0x41, 0x59, 0x26, 0x53, 0x59, 0x00],
            NsisCompressor.Bzip2NonSolid => CompressedBlock([0x31, 0x05, 0x41, 0x59, 0x26, 0x53, 0x59, 0x00]),
            NsisCompressor.LzmaBcjSolid => [0x01, .. Lzma(header)],
            NsisCompressor.CorruptDeflateNonSolid => CompressedBlock([0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89]),
            NsisCompressor.CorruptLzmaSolid => [0x5D, 0x00, 0x00, 0x80, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            NsisCompressor.NoDataAtAll => [],
            _ => throw new ArgumentOutOfRangeException(nameof(compressor)),
        };

        byte[] archive = new byte[28 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), 0xDEADBEEF);
        "NullsoftInst"u8.CopyTo(archive.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(20), declaredSize);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(24), archive.Length);
        data.CopyTo(archive, 28);
        return archive;
    }

    private static byte[] CompressedBlock(byte[] compressed)
        => [.. SizePrefix(0x80000000u | (uint)compressed.Length), .. compressed];

    private static byte[] SizePrefix(uint value)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, value);
        return prefix;
    }

    /// <summary>A raw deflate stream, the NSIS "zlib" mode (no zlib wrapper).</summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Raw LZMA1 the way NSIS stores it: the 5 property bytes, then the stream.</summary>
    private static byte[] Lzma(byte[] data)
    {
        using var output = new MemoryStream();
        byte[] properties;
        using (var lzma = new LzmaStream(new LzmaEncoderProperties(), false, output))
        {
            properties = lzma.Properties;
            lzma.Write(data);
        }

        return [.. properties, .. output.ToArray()];
    }

    /// <summary>One piece of an encoded NSIS string.</summary>
    public readonly record struct Token(char Code, string? Literal, byte Low, byte High)
    {
        public static Token Lit(string text) => new('\0', text, 0, 0);

        /// <summary>NS_LANG_CODE: a language-table string by index (CODE_SHORT-encoded).</summary>
        public static Token Lang(int index) => new('\x01', null, EncodeLow(index), EncodeHigh(index));

        /// <summary>NS_SHELL_CODE with raw CSIDL bytes (current user, all users).</summary>
        public static Token Shell(byte userFolder, byte commonFolder) => new('\x02', null, userFolder, commonFolder);

        /// <summary>
        /// $PROGRAMFILES / $PROGRAMFILES64 the way makensis encodes them: a registry-resolved
        /// shell folder (bit 0x80) whose value name "ProgramFilesDir" sits at string offset 1,
        /// read from the 64-bit view when bit 0x40 is set.
        /// </summary>
        public static Token ShellProgramFiles(bool x64)
            => new('\x02', null, (byte)(0x80 | (x64 ? 0x40 : 0) | StringsBlock.ProgramFilesDirOffset), 0);

        /// <summary>A registry-resolved shell folder with an unknown value name and a literal default.</summary>
        public static Token ShellRegistryUnknown()
            => new('\x02', null, (byte)(0x80 | StringsBlock.OtherDirOffset), StringsBlock.OtherDefaultOffset);

        /// <summary>NS_VAR_CODE: a variable by index (CODE_SHORT-encoded); 21 is $INSTDIR.</summary>
        public static Token Var(int index) => new('\x03', null, EncodeLow(index), EncodeHigh(index));

        /// <summary>NS_SKIP_CODE: the next character is a literal.</summary>
        public static Token Skip(char literal) => new('\x04', literal.ToString(), 0, 0);

        // CODE_SHORT: 7 bits per byte, high bits set so no parameter byte is ever NUL.
        private static byte EncodeLow(int value) => (byte)((value & 0x7F) | 0x80);

        private static byte EncodeHigh(int value) => (byte)(((value >> 7) & 0x7F) | 0x80);
    }

    /// <summary>
    /// The strings block: starts with the empty string, then the plain registry value names
    /// NSIS keeps at small offsets so shell codes can reference them in six bits, then
    /// whatever the script adds. Offsets are in character units — bytes when ANSI (encoded as
    /// Latin-1), UTF-16 code units when Unicode.
    /// </summary>
    private sealed class StringsBlock
    {
        public const byte ProgramFilesDirOffset = 1;
        public const byte CommonFilesDirOffset = 17;
        public const byte OtherDirOffset = 32;
        public const byte OtherDefaultOffset = 41;

        private readonly List<byte> _bytes = [];
        private readonly bool _unicode;

        public StringsBlock(bool unicode)
        {
            _unicode = unicode;
            AppendChar('\0');
            Add([Token.Lit("ProgramFilesDir")]);  // Offset 1.
            Add([Token.Lit("CommonFilesDir")]);   // Offset 17.
            Add([Token.Lit("OtherDir")]);         // Offset 32.
            Add([Token.Lit(@"C:\Other")]);        // Offset 41.
        }

        /// <summary>Appends an encoded string and returns its offset in character units.</summary>
        public int Add(IReadOnlyList<Token> tokens)
        {
            int offset = _bytes.Count / (_unicode ? 2 : 1);
            foreach (Token token in tokens)
            {
                if (token.Code == '\0')
                {
                    AppendLiteral(token.Literal!);
                }
                else
                {
                    AppendChar(token.Code);
                    if (token.Code == '\x04')
                    {
                        AppendLiteral(token.Literal!);
                    }
                    else if (_unicode)
                    {
                        AppendChar((char)(token.Low | (token.High << 8)));
                    }
                    else
                    {
                        _bytes.Add(token.Low);
                        _bytes.Add(token.High);
                    }
                }
            }

            AppendChar('\0');
            return offset;
        }

        public byte[] ToArray() => [.. _bytes];

        private void AppendLiteral(string text)
        {
            foreach (char c in text)
            {
                AppendChar(c);
            }
        }

        private void AppendChar(char c)
        {
            if (_unicode)
            {
                _bytes.Add((byte)c);
                _bytes.Add((byte)((uint)c >> 8));
            }
            else
            {
                _bytes.Add(Encoding.Latin1.GetBytes([c])[0]);
            }
        }
    }
}
