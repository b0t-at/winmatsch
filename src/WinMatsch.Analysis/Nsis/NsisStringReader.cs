using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WinMatsch.Analysis.Nsis;

/// <summary>
/// Decodes NSIS strings (NSIS 3, <c>Source/exehead/util.c</c> <c>GetNSISString</c>). A string
/// reference is an offset into the strings block in character units — bytes in ANSI builds,
/// UTF-16 code units in Unicode builds. Characters 1–4 are escape codes (identical in both
/// charsets since NSIS 3; NSIS 2's ANSI codes 252–255 are not interpreted, so NSIS 2 strings
/// decode correctly only when they contain no variable references):
/// <list type="bullet">
/// <item>1 <c>NS_LANG_CODE</c>: a language-table string; the next two bytes hold the table
/// index (each byte contributes its low 7 bits, low byte first — <c>DECODE_SHORT</c>).
/// Resolved against the build's default (first) language table, recursively.</item>
/// <item>2 <c>NS_SHELL_CODE</c>: a shell folder; the next two bytes are the current-user and
/// the all-users folder id. An id with bit 0x80 set is resolved from the registry value under
/// <c>Software\Microsoft\Windows\CurrentVersion</c> whose name is stored as a plain string at
/// offset (id &amp; 0x3F) of the strings block, read from the 64-bit registry view when bit
/// 0x40 is set — this is how NSIS 3 encodes <c>$PROGRAMFILES</c>/<c>$PROGRAMFILES64</c>
/// (ProgramFilesDir) and <c>$COMMONFILES</c>/<c>$COMMONFILES64</c> (CommonFilesDir). Other
/// ids are CSIDL values mapped through a table of the common NSIS constants; unknown ids
/// decode to nothing.</item>
/// <item>3 <c>NS_VAR_CODE</c>: a variable; the next two bytes hold the index
/// (<c>DECODE_SHORT</c>): 0–9 → <c>$0</c>–<c>$9</c>, 10–19 → <c>$R0</c>–<c>$R9</c>, then the
/// builtins <c>$CMDLINE</c> (20), <c>$INSTDIR</c>, <c>$OUTDIR</c>, <c>$EXEDIR</c>,
/// <c>$LANGUAGE</c>, <c>$TEMP</c>, <c>$PLUGINSDIR</c>, <c>$EXEPATH</c>, <c>$EXEFILE</c>,
/// <c>$HWNDPARENT</c> (29); higher indices are script-defined variables, rendered as
/// <c>$__VARn__</c>.</item>
/// <item>4 <c>NS_SKIP_CODE</c>: the next character is a literal.</item>
/// </list>
/// In Unicode builds each two-byte parameter occupies one UTF-16 code unit (low byte first).
/// ANSI literals are decoded as Latin-1: the build's real code page is not recorded in the
/// installer, and Latin-1 keeps every byte round-trippable.
/// </summary>
internal sealed class NsisStringReader
{
    private const char LangCode = '\x01';
    private const char ShellCode = '\x02';
    private const char VarCode = '\x03';
    private const char SkipCode = '\x04';

    private const int MaxRecursionDepth = 16;

    private static readonly string[] _builtinVariables =
    [
        "$CMDLINE", "$INSTDIR", "$OUTDIR", "$EXEDIR", "$LANGUAGE",
        "$TEMP", "$PLUGINSDIR", "$EXEPATH", "$EXEFILE", "$HWNDPARENT",
    ];

    // The CSIDL values NSIS's shell constants compile to (current-user variant), from
    // Source/build.cpp init_shellconstantvalues plus the Windows CSIDL table.
    private static readonly Dictionary<byte, string> _shellFolders = new()
    {
        [0x02] = "$SMPROGRAMS",
        [0x05] = "$DOCUMENTS",
        [0x06] = "$FAVORITES",
        [0x07] = "$SMSTARTUP",
        [0x08] = "$RECENT",
        [0x09] = "$SENDTO",
        [0x0B] = "$STARTMENU",
        [0x0D] = "$MUSIC",
        [0x0E] = "$VIDEOS",
        [0x10] = "$DESKTOP",
        [0x13] = "$NETHOOD",
        [0x14] = "$FONTS",
        [0x15] = "$TEMPLATES",
        [0x1A] = "$APPDATA",
        [0x1B] = "$PRINTHOOD",
        [0x1C] = "$LOCALAPPDATA",
        [0x20] = "$INTERNET_CACHE",
        [0x21] = "$COOKIES",
        [0x22] = "$HISTORY",
        [0x23] = "$APPDATA",  // CSIDL_COMMON_APPDATA: the all-users variant of $APPDATA.
        [0x24] = "$WINDIR",
        [0x25] = "$SYSDIR",
        [0x26] = "$PROGRAMFILES",
        [0x27] = "$PICTURES",
        [0x28] = "$PROFILE",
        [0x2A] = "$PROGRAMFILES32",
        [0x2B] = "$COMMONFILES",
        [0x2E] = "$ADMINTOOLS",
        [0x3B] = "$CDBURN_AREA",
    };

    private readonly byte[] _strings;
    private readonly bool _isUnicode;
    private readonly IReadOnlyList<int> _langStrings;

    public NsisStringReader(NsisHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _strings = header.Strings.ToArray();
        _isUnicode = header.IsUnicode;
        _langStrings = header.GetFirstLangTable()?.Strings ?? [];
    }

    /// <summary>
    /// Decodes the string at <paramref name="reference"/> character units into the strings
    /// block. Returns null for references outside the block (corrupt or absent fields) and
    /// for the empty string, which NSIS uses for "not set".
    /// </summary>
    public string? Read(int reference)
    {
        string value = ReadCore(reference, MaxRecursionDepth);
        return value.Length == 0 ? null : value;
    }

    private string ReadCore(int reference, int depth)
    {
        int charSize = _isUnicode ? 2 : 1;
        long byteOffset = (long)reference * charSize;
        if (depth <= 0 || reference < 0 || byteOffset >= _strings.Length)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        int position = (int)byteOffset;
        while (true)
        {
            char current = ReadChar(ref position);
            if (current == '\0')
            {
                break;
            }

            if (current > SkipCode)
            {
                result.Append(current);
            }
            else if (current == SkipCode)
            {
                char literal = ReadChar(ref position);
                if (literal == '\0')
                {
                    break;
                }

                result.Append(literal);
            }
            else
            {
                (byte low, byte high) = ReadParameter(ref position);
                switch (current)
                {
                    case LangCode:
                        AppendLangString(result, DecodeShort(low, high), depth);
                        break;
                    case ShellCode:
                        AppendShellFolder(result, low, high, depth);
                        break;
                    case VarCode:
                        AppendVariable(result, DecodeShort(low, high));
                        break;
                    default:
                        break; // Unreachable: 1..4 are all handled.
                }
            }
        }

        return result.ToString();
    }

    /// <summary>The next character, or NUL at (and beyond) the end of the block.</summary>
    private char ReadChar(ref int position)
    {
        if (_isUnicode)
        {
            if (position + 2 > _strings.Length)
            {
                position = _strings.Length;
                return '\0';
            }

            char value = (char)BinaryPrimitives.ReadUInt16LittleEndian(_strings.AsSpan(position));
            position += 2;
            return value;
        }

        return position < _strings.Length ? (char)_strings[position++] : '\0';
    }

    /// <summary>The two parameter bytes after an escape code: one UTF-16 unit or two bytes.</summary>
    private (byte Low, byte High) ReadParameter(ref int position)
    {
        char packed = ReadChar(ref position);
        if (_isUnicode)
        {
            return ((byte)packed, (byte)((uint)packed >> 8));
        }

        char high = ReadChar(ref position);
        return ((byte)packed, (byte)high);
    }

    /// <summary>DECODE_SHORT: both parameter bytes contribute their low 7 bits, low byte first.</summary>
    private static int DecodeShort(byte low, byte high) => (low & 0x7F) | ((high & 0x7F) << 7);

    private void AppendLangString(StringBuilder result, int index, int depth)
    {
        if (index >= 0 && index < _langStrings.Count)
        {
            result.Append(ReadCore(_langStrings[index], depth - 1));
        }
    }

    private static void AppendVariable(StringBuilder result, int index)
    {
        string name = index switch
        {
            >= 0 and <= 9 => "$" + (char)('0' + index),
            >= 10 and <= 19 => "$R" + (char)('0' + index - 10),
            >= 20 and <= 29 => _builtinVariables[index - 20],
            _ => "$__VAR" + index.ToString(CultureInfo.InvariantCulture) + "__",
        };
        result.Append(name);
    }

    private void AppendShellFolder(StringBuilder result, byte userFolder, byte defaultFolder, int depth)
    {
        if ((userFolder & 0x80) != 0)
        {
            // Registry-resolved folder: the value name lives as a plain string at the low
            // six bits; bit 0x40 selects the 64-bit registry view.
            string valueName = ReadCore(userFolder & 0x3F, depth - 1);
            string suffix = (userFolder & 0x40) != 0 ? "64" : "";
            if (valueName.Equals("ProgramFilesDir", StringComparison.OrdinalIgnoreCase))
            {
                result.Append("$PROGRAMFILES").Append(suffix);
            }
            else if (valueName.Equals("CommonFilesDir", StringComparison.OrdinalIgnoreCase))
            {
                result.Append("$COMMONFILES").Append(suffix);
            }
            else
            {
                // Unknown registry folder: fall back to the encoded default, the way the
                // NSIS stub does when the registry value is missing.
                result.Append(ReadCore(defaultFolder, depth - 1));
            }

            return;
        }

        if (_shellFolders.TryGetValue(userFolder, out string? name))
        {
            result.Append(name);
        }
    }
}
