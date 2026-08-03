using System.Buffers.Binary;
using System.Text;

namespace WinMatsch.Analysis.Msi;

/// <summary>
/// The MSI string pool: the <c>_StringPool</c> stream holds a codepage header followed by
/// (length, refcount) entries; the <c>_StringData</c> stream holds the concatenated encoded
/// string bytes. String references in table streams are 1-based indexes into this pool,
/// serialized as two bytes — or three when the pool header's long-refs bit (bit 31) is set.
/// </summary>
internal sealed class MsiStringPool
{
    private const uint LongStringRefsBit = 0x8000_0000;
    private const int Utf8Codepage = 65001;

    private readonly string[] _strings;

    private MsiStringPool(string[] strings, int codepage, bool longStringRefs)
    {
        _strings = strings;
        Codepage = codepage;
        LongStringRefs = longStringRefs;
    }

    /// <summary>The Windows codepage the string data is encoded with.</summary>
    public int Codepage { get; }

    /// <summary>Whether string references in table streams are three bytes instead of two.</summary>
    public bool LongStringRefs { get; }

    /// <summary>The number of bytes one serialized string reference occupies in a table stream.</summary>
    public int StringRefWidth => LongStringRefs ? 3 : 2;

    /// <summary>Parses the pool from the raw <c>_StringPool</c> and <c>_StringData</c> stream contents.</summary>
    /// <exception cref="InvalidDataException">The streams are truncated or inconsistent.</exception>
    public static MsiStringPool Read(ReadOnlySpan<byte> pool, ReadOnlySpan<byte> data)
    {
        if (pool.Length < 4)
        {
            throw new InvalidDataException("The MSI _StringPool stream is too short to contain the codepage header.");
        }

        uint header = BinaryPrimitives.ReadUInt32LittleEndian(pool);
        bool longStringRefs = (header & LongStringRefsBit) != 0;
        int codepage = (int)(header & ~LongStringRefsBit);

        var strings = new List<string>();
        int poolOffset = 4;
        int dataOffset = 0;
        while (poolOffset + 4 <= pool.Length)
        {
            uint length = BinaryPrimitives.ReadUInt16LittleEndian(pool[poolOffset..]);
            ushort refcount = BinaryPrimitives.ReadUInt16LittleEndian(pool[(poolOffset + 2)..]);
            poolOffset += 4;

            // A zero length with a non-zero refcount marks a large string: the real length
            // follows as a 32-bit value. (Zero length with zero refcount is an empty slot.)
            if (length == 0 && refcount != 0)
            {
                if (poolOffset + 4 > pool.Length)
                {
                    throw new InvalidDataException("The MSI _StringPool stream is truncated inside a large-string entry.");
                }

                length = BinaryPrimitives.ReadUInt32LittleEndian(pool[poolOffset..]);
                poolOffset += 4;
            }

            if (length > int.MaxValue || dataOffset + (int)length > data.Length)
            {
                throw new InvalidDataException("The MSI _StringData stream is shorter than the lengths recorded in _StringPool.");
            }

            strings.Add(DecodeString(data.Slice(dataOffset, (int)length), codepage));
            dataOffset += (int)length;
        }

        return new MsiStringPool([.. strings], codepage, longStringRefs);
    }

    /// <summary>
    /// Returns the string for a 1-based pool reference. A null reference (0) and references
    /// beyond the pool resolve to null, mirroring how Windows Installer treats them.
    /// </summary>
    public string? Get(int reference)
        => reference >= 1 && reference <= _strings.Length ? _strings[reference - 1] : null;

    /// <summary>
    /// Reads one serialized string reference (2 or 3 bytes, per <see cref="LongStringRefs"/>)
    /// from the given position. Returns 0 for a null reference.
    /// </summary>
    public int ReadStringRef(ReadOnlySpan<byte> stream, int offset)
    {
        int reference = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
        if (LongStringRefs)
        {
            reference |= stream[offset + 2] << 16;
        }

        return reference;
    }

    // InvariantGlobalization is enabled, so arbitrary Windows codepages are unavailable.
    // UTF-8 pools decode exactly; everything else decodes as Latin-1, which is byte-identical
    // for ASCII (the overwhelmingly common case for MSI property names and values) and a
    // close approximation for Windows-1252, the usual MSI codepage.
    private static string DecodeString(ReadOnlySpan<byte> bytes, int codepage)
        => codepage == Utf8Codepage ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
}
