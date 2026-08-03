using System.Buffers.Binary;

namespace WinMatsch.Analysis.Nsis;

/// <summary>
/// The decompressed NSIS installer header (NSIS <c>Source/exehead/fileform.h</c>, struct
/// <c>header</c>), assuming the layout of official NSIS 3 release builds — every layout-
/// affecting feature (<c>NSIS_CONFIG_*</c>/<c>NSIS_SUPPORT_*</c>) compiled in, as in the
/// shipped stubs: flags at 0; eight 8-byte block headers {offset, count} at 4 (pages,
/// sections, entries, strings, langtables, ctlcolors, bgfont, data); <c>langtable_size</c> at
/// 100; <c>install_directory_ptr</c> at 280. Block offsets are relative to the header start.
/// Instructions are 28-byte records of one opcode and six parameters; string parameters are
/// offsets into the strings block in character units. Whether the build is Unicode (NSIS 3
/// with <c>Unicode true</c>: UTF-16LE strings) or ANSI is not declared anywhere and is
/// inferred from the strings block, which always starts with the empty string: a second zero
/// byte can only be a UTF-16 terminator.
/// </summary>
internal sealed class NsisHeader
{
    private const int BlockCount = 8;
    private const int BlockHeadersOffset = 4;
    private const int LangtableSizeOffset = 100;
    private const int InstallDirectoryPtrOffset = 280;
    private const int FixedPartSize = 300; // Through str_wininit; blocks may start here.

    private const int EntriesBlock = 2;
    private const int StringsBlock = 3;
    private const int LangtablesBlock = 4;

    /// <summary>One instruction: 28 bytes, an EW_* opcode and six parameters.</summary>
    public const int EntrySize = 28;

    private readonly byte[] _data;
    private readonly (int Offset, int Count)[] _blocks;

    private NsisHeader(byte[] data, (int Offset, int Count)[] blocks, bool isUnicode)
    {
        _data = data;
        _blocks = blocks;
        IsUnicode = isUnicode;
    }

    /// <summary>Whether strings are UTF-16LE (Unicode build) rather than 8-bit (ANSI build).</summary>
    public bool IsUnicode { get; }

    /// <summary>The number of instructions in the entries block.</summary>
    public int EntryCount => _blocks[EntriesBlock].Count;

    /// <summary>The number of language tables declared by the installer.</summary>
    public int LanguageTableCount => _blocks[LangtablesBlock].Count;

    /// <summary>The strings block, in raw bytes.</summary>
    public ReadOnlySpan<byte> Strings
        => _data.AsSpan(_blocks[StringsBlock].Offset, _blocks[StringsBlock].Count);

    /// <summary>
    /// The <c>install_directory_ptr</c> string reference (the default <c>$INSTDIR</c>), in
    /// character units into the strings block; 0 references the empty string.
    /// </summary>
    public int InstallDirectoryPtr => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(InstallDirectoryPtrOffset));

    /// <summary>Parses and bounds-checks the header data.</summary>
    /// <exception cref="InvalidDataException">The header is too small or a block lies outside it.</exception>
    public static NsisHeader Parse(byte[] data)
    {
        if (data.Length < FixedPartSize)
        {
            throw new InvalidDataException(
                $"The NSIS installer header is only {data.Length} bytes; the NSIS 3 fixed part needs {FixedPartSize}.");
        }

        var blocks = new (int Offset, int Count)[BlockCount];
        for (int i = 0; i < BlockCount; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(BlockHeadersOffset + (i * 8)));
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(BlockHeadersOffset + (i * 8) + 4));
            if (offset < 0 || count < 0 || offset > data.Length)
            {
                throw new InvalidDataException($"NSIS installer header block {i} lies outside the header data.");
            }

            blocks[i] = (offset, count);
        }

        // The strings block carries no byte length in its count field; it runs to the next
        // block in file order. makensis places langtables right after strings, but other
        // writers (e.g. Tauri's NSIS builds) order the blocks differently, so the nearest
        // following block offset — or the header end — bounds the region.
        (int stringsOffset, _) = blocks[StringsBlock];
        int stringsEnd = data.Length;
        for (int i = 0; i < BlockCount; i++)
        {
            if (i != StringsBlock && blocks[i].Offset > stringsOffset && blocks[i].Offset < stringsEnd)
            {
                stringsEnd = blocks[i].Offset;
            }
        }

        blocks[StringsBlock] = (stringsOffset, stringsEnd - stringsOffset);

        long entriesEnd = blocks[EntriesBlock].Offset + ((long)blocks[EntriesBlock].Count * EntrySize);
        if (entriesEnd > data.Length)
        {
            throw new InvalidDataException("The NSIS entries block extends past the end of the header data.");
        }

        ReadOnlySpan<byte> strings = data.AsSpan(stringsOffset, stringsEnd - stringsOffset);
        bool isUnicode = strings.Length >= 2 && strings[0] == 0 && strings[1] == 0;
        return new NsisHeader(data, blocks, isUnicode);
    }

    /// <summary>Reads instruction <paramref name="index"/> as its opcode and six parameters.</summary>
    public NsisEntry GetEntry(int index)
    {
        ReadOnlySpan<byte> entry = _data.AsSpan(_blocks[EntriesBlock].Offset + (index * EntrySize), EntrySize);
        return new NsisEntry(
            BinaryPrimitives.ReadInt32LittleEndian(entry),
            BinaryPrimitives.ReadInt32LittleEndian(entry[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(entry[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(entry[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(entry[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(entry[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(entry[24..]));
    }

    /// <summary>
    /// The first language table, or null when the block is empty. Each table is
    /// <c>langtable_size</c> bytes: LANGID at 0, dialog offset at 2, RTL flag at 6, then the
    /// language strings as 32-bit string references from byte 10 (packed, no alignment).
    /// The first table is the build's default language — the one whose strings
    /// <c>$(...)</c> references resolve to here.
    /// </summary>
    public NsisLangTable? GetFirstLangTable()
    {
        (int offset, int count) = _blocks[LangtablesBlock];
        int size = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(LangtableSizeOffset));
        if (count == 0 || size < 10 || offset + size > _data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> table = _data.AsSpan(offset, size);
        int[] strings = new int[(size - 10) / 4];
        for (int i = 0; i < strings.Length; i++)
        {
            strings[i] = BinaryPrimitives.ReadInt32LittleEndian(table[(10 + (i * 4))..]);
        }

        return new NsisLangTable(BinaryPrimitives.ReadUInt16LittleEndian(table), strings);
    }
}

/// <summary>One NSIS instruction: the EW_* opcode and its six parameters.</summary>
internal readonly record struct NsisEntry(int Which, int Parm0, int Parm1, int Parm2, int Parm3, int Parm4, int Parm5);

/// <summary>A language table: its LCID and its language strings (string references).</summary>
internal sealed record NsisLangTable(ushort LanguageId, IReadOnlyList<int> Strings);
