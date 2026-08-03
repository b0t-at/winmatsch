using System.Buffers.Binary;
using System.Text;

namespace WinMatsch.Analysis.Pe;

/// <summary>
/// The string values of a PE <c>VS_VERSIONINFO</c> resource, taken from the first
/// <c>StringTable</c> regardless of its language. Every property is null when the file has no
/// version resource or the string is absent or empty.
/// </summary>
public sealed class VersionInfo
{
    public string? ProductName { get; init; }

    public string? CompanyName { get; init; }

    public string? LegalCopyright { get; init; }

    public string? ProductVersion { get; init; }

    public string? FileVersion { get; init; }

    public string? OriginalFilename { get; init; }

    public string? FileDescription { get; init; }

    /// <summary>
    /// Parses the version resource. The format is a tree of variable-length blocks
    /// (<c>VS_VERSIONINFO</c> → <c>StringFileInfo</c> → <c>StringTable</c> → <c>String</c>),
    /// each carrying a UTF-16 key and padded to 32-bit boundaries relative to the start of the
    /// resource. Structural damage yields an instance with the salvageable values only.
    /// </summary>
    internal static VersionInfo Parse(ReadOnlySpan<byte> data)
    {
        string? productName = null;
        string? companyName = null;
        string? legalCopyright = null;
        string? productVersion = null;
        string? fileVersion = null;
        string? originalFilename = null;
        string? fileDescription = null;

        if (TryReadBlock(data, 0, out Block root)
            && TryFindChild(data, in root, "StringFileInfo", out Block stringFileInfo)
            && TryReadBlock(data, stringFileInfo.ChildrenStart, out Block stringTable)
            && stringTable.End <= stringFileInfo.End)
        {
            int offset = stringTable.ChildrenStart;
            while (offset < stringTable.End && TryReadBlock(data, offset, out Block entry) && entry.End <= stringTable.End)
            {
                string? value = ReadTextValue(data, in entry);
                if (entry.Key.Equals("ProductName", StringComparison.OrdinalIgnoreCase))
                {
                    productName = value;
                }
                else if (entry.Key.Equals("CompanyName", StringComparison.OrdinalIgnoreCase))
                {
                    companyName = value;
                }
                else if (entry.Key.Equals("LegalCopyright", StringComparison.OrdinalIgnoreCase))
                {
                    legalCopyright = value;
                }
                else if (entry.Key.Equals("ProductVersion", StringComparison.OrdinalIgnoreCase))
                {
                    productVersion = value;
                }
                else if (entry.Key.Equals("FileVersion", StringComparison.OrdinalIgnoreCase))
                {
                    fileVersion = value;
                }
                else if (entry.Key.Equals("OriginalFilename", StringComparison.OrdinalIgnoreCase))
                {
                    originalFilename = value;
                }
                else if (entry.Key.Equals("FileDescription", StringComparison.OrdinalIgnoreCase))
                {
                    fileDescription = value;
                }

                offset = Align4(offset + entry.Length);
            }
        }

        return new VersionInfo
        {
            ProductName = productName,
            CompanyName = companyName,
            LegalCopyright = legalCopyright,
            ProductVersion = productVersion,
            FileVersion = fileVersion,
            OriginalFilename = originalFilename,
            FileDescription = fileDescription,
        };
    }

    /// <summary>Scans the children of <paramref name="parent"/> for the first block with the given key.</summary>
    private static bool TryFindChild(ReadOnlySpan<byte> data, in Block parent, string key, out Block child)
    {
        int offset = parent.ChildrenStart;
        while (offset < parent.End && TryReadBlock(data, offset, out child) && child.End <= parent.End)
        {
            if (child.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            offset = Align4(offset + child.Length);
        }

        child = default;
        return false;
    }

    /// <summary>
    /// Reads the common block header: <c>wLength</c>, <c>wValueLength</c>, <c>wType</c>, the
    /// null-terminated UTF-16 key, then padding to a 32-bit boundary before the value and
    /// again before the children.
    /// </summary>
    private static bool TryReadBlock(ReadOnlySpan<byte> data, int start, out Block block)
    {
        block = default;
        if (start < 0 || start + 6 > data.Length)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[start..]);
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 2)..]);
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 4)..]);
        if (length < 6 || start + length > data.Length)
        {
            return false;
        }

        int end = start + length;
        var key = new StringBuilder();
        int position = start + 6;
        while (position + 2 <= end)
        {
            char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            position += 2;
            if (c == '\0')
            {
                break;
            }

            key.Append(c);
        }

        // wValueLength counts words for textual values (wType == 1) and bytes for binary ones.
        int valueStart = Align4(position);
        int valueBytes = type == 1 ? valueLength * 2 : valueLength;
        block = new Block
        {
            Start = start,
            Length = length,
            ValueLength = valueLength,
            Type = type,
            Key = key.ToString(),
            ValueStart = valueStart,
            ChildrenStart = Math.Min(Align4(valueStart + valueBytes), end),
        };
        return true;
    }

    /// <summary>Decodes a textual block value, clamped to the block, trimmed at the null terminator.</summary>
    private static string? ReadTextValue(ReadOnlySpan<byte> data, in Block block)
    {
        if (block.Type != 1 || block.ValueLength == 0)
        {
            return null;
        }

        int available = Math.Min(block.ValueLength * 2, block.End - block.ValueStart);
        if (available <= 0 || block.ValueStart + available > data.Length)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(data.Slice(block.ValueStart, available));
        int terminator = text.IndexOf('\0', StringComparison.Ordinal);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return text.Length == 0 ? null : text;
    }

    private static int Align4(int offset) => (offset + 3) & ~3;

    private struct Block
    {
        public int Start;
        public int Length;
        public int ValueLength;
        public int Type;
        public string Key;
        public int ValueStart;
        public int ChildrenStart;

        public readonly int End => Start + Length;
    }
}
