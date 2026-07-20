using System.Text;
using OpenMcdf;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Builds small but structurally valid MSI databases for tests. Like <see cref="PeFixtures"/>,
/// the writer is implemented independently from the production reader, straight from the
/// documented on-disk layout: CFB stream-name encoding, the string pool, column-major table
/// streams and the OLE property set of the SummaryInformation stream.
/// </summary>
internal static class MsiFixtures
{
    private const string Base64Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";
    private const int Utf8Codepage = 65001;

    // Column type bits: 0x0800 string, 0x2000 key, 0x0200 localizable, 0x0100 valid, low byte = width.
    private const ushort PropertyColumnType = 0x2948; // s72, primary key
    private const ushort ValueColumnType = 0x0B00;    // l0, localizable string

    /// <summary>Encodes a logical MSI stream name into its compressed CFB representation.</summary>
    public static string EncodeStreamName(string name, bool isTable)
    {
        var builder = new StringBuilder();
        if (isTable)
        {
            builder.Append('\u4840');
        }

        int i = 0;
        while (i < name.Length)
        {
            int first = Base64Alphabet.IndexOf(name[i]);
            if (first < 0)
            {
                builder.Append(name[i]);
                i++;
                continue;
            }

            int second = i + 1 < name.Length ? Base64Alphabet.IndexOf(name[i + 1]) : -1;
            if (second >= 0)
            {
                builder.Append((char)(0x3800 + first + (second << 6)));
                i += 2;
            }
            else
            {
                builder.Append((char)(0x4800 + first));
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds an MSI database containing a Property table with the given rows and, unless
    /// disabled, a SummaryInformation stream carrying Template (PID 7), Creating Application
    /// (PID 18) and Comments (PID 6). A null value skips the respective property.
    /// </summary>
    public static byte[] BuildMsi(
        (string Name, string Value)[] properties,
        string? template = "x64;1033",
        string? creatingApplication = "WiX Toolset v4",
        string? comments = null,
        bool longStringRefs = false,
        bool includeSummaryInformation = true)
    {
        var pool = new StringPoolWriter();
        int propertyStringId = pool.Add("Property"); // Table name and first column name.
        int valueStringId = pool.Add("Value");
        var rows = new List<(int NameId, int ValueId)>();
        foreach ((string name, string value) in properties)
        {
            rows.Add((pool.Add(name), pool.Add(value)));
        }

        byte[] tablesStream = BuildStream(writer => WriteStringRef(writer, propertyStringId, longStringRefs));

        byte[] columnsStream = BuildStream(writer =>
        {
            // Column-major: Table refs, then Numbers, then Name refs, then Types — for both rows.
            WriteStringRef(writer, propertyStringId, longStringRefs);
            WriteStringRef(writer, propertyStringId, longStringRefs);
            WriteInt16Cell(writer, 1);
            WriteInt16Cell(writer, 2);
            WriteStringRef(writer, propertyStringId, longStringRefs);
            WriteStringRef(writer, valueStringId, longStringRefs);
            WriteInt16Cell(writer, PropertyColumnType);
            WriteInt16Cell(writer, ValueColumnType);
        });

        byte[] propertyStream = BuildStream(writer =>
        {
            foreach ((int nameId, _) in rows)
            {
                WriteStringRef(writer, nameId, longStringRefs);
            }

            foreach ((_, int valueId) in rows)
            {
                WriteStringRef(writer, valueId, longStringRefs);
            }
        });

        using var buffer = new MemoryStream();
        using (var root = RootStorage.Create(buffer, flags: StorageModeFlags.LeaveOpen))
        {
            WriteCfbStream(root, EncodeStreamName("_StringPool", isTable: true), pool.BuildPoolStream(longStringRefs));
            WriteCfbStream(root, EncodeStreamName("_StringData", isTable: true), pool.BuildDataStream());
            WriteCfbStream(root, EncodeStreamName("_Tables", isTable: true), tablesStream);
            WriteCfbStream(root, EncodeStreamName("_Columns", isTable: true), columnsStream);
            WriteCfbStream(root, EncodeStreamName("Property", isTable: true), propertyStream);
            if (includeSummaryInformation)
            {
                WriteCfbStream(root, "\u0005SummaryInformation", BuildSummaryInformation(template, creatingApplication, comments));
            }
        }

        return buffer.ToArray();
    }

    private static byte[] BuildStream(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    private static void WriteCfbStream(RootStorage root, string name, byte[] content)
    {
        using CfbStream stream = root.CreateStream(name);
        stream.Write(content, 0, content.Length);
    }

    /// <summary>Writes a string pool reference: two bytes, or three when long refs are enabled.</summary>
    private static void WriteStringRef(BinaryWriter writer, int reference, bool longStringRefs)
    {
        writer.Write((ushort)(reference & 0xFFFF));
        if (longStringRefs)
        {
            writer.Write((byte)((reference >> 16) & 0xFF));
        }
    }

    /// <summary>Writes a two-byte integer cell, biased by 0x8000; zero would mean null.</summary>
    private static void WriteInt16Cell(BinaryWriter writer, int value)
        => writer.Write(unchecked((ushort)(value + 0x8000)));

    /// <summary>
    /// Writes a minimal MS-OLEPS property set stream: header, one set (the SummaryInformation
    /// FMTID), a section with the codepage (PID 1, VT_I2) plus the given VT_LPSTR properties.
    /// </summary>
    private static byte[] BuildSummaryInformation(string? template, string? creatingApplication, string? comments)
    {
        var values = new List<(uint Id, byte[] Value)> { (1u, BuildI2Value(1252)) };
        if (comments is not null)
        {
            values.Add((6u, BuildLpstrValue(comments)));
        }

        if (template is not null)
        {
            values.Add((7u, BuildLpstrValue(template)));
        }

        if (creatingApplication is not null)
        {
            values.Add((18u, BuildLpstrValue(creatingApplication)));
        }

        return BuildStream(writer =>
        {
            const int SectionStart = 48; // 28-byte header + one 20-byte (FMTID, offset) entry.
            writer.Write((ushort)0xFFFE);                 // Byte order.
            writer.Write((ushort)0);                      // Version.
            writer.Write(0x00020006u);                    // System identifier.
            writer.Write(new byte[16]);                   // CLSID.
            writer.Write(1u);                             // Number of property sets.
            writer.Write(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9").ToByteArray());
            writer.Write((uint)SectionStart);

            int headerSize = 8 + (values.Count * 8);
            int sectionSize = headerSize;
            foreach ((_, byte[] value) in values)
            {
                sectionSize += value.Length;
            }

            writer.Write((uint)sectionSize);
            writer.Write((uint)values.Count);
            int valueOffset = headerSize;
            foreach ((uint id, byte[] value) in values)
            {
                writer.Write(id);
                writer.Write((uint)valueOffset);
                valueOffset += value.Length;
            }

            foreach ((_, byte[] value) in values)
            {
                writer.Write(value);
            }
        });
    }

    private static byte[] BuildI2Value(short value) => BuildStream(writer =>
    {
        writer.Write(2u); // VT_I2
        writer.Write(value);
        writer.Write((ushort)0); // Padding to a four-byte boundary.
    });

    private static byte[] BuildLpstrValue(string value) => BuildStream(writer =>
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(30u); // VT_LPSTR
        writer.Write((uint)(bytes.Length + 1)); // Length includes the terminating NUL.
        writer.Write(bytes);
        writer.Write((byte)0);
        for (int pad = (bytes.Length + 1) % 4; pad != 0 && pad < 4; pad++)
        {
            writer.Write((byte)0);
        }
    });

    /// <summary>
    /// Collects unique strings and serializes the <c>_StringPool</c>/<c>_StringData</c> pair.
    /// An empty string is the null reference (0) and is never stored in the pool.
    /// </summary>
    private sealed class StringPoolWriter
    {
        private readonly List<string> _strings = [];

        public int Add(string value)
        {
            if (value.Length == 0)
            {
                return 0;
            }

            int index = _strings.IndexOf(value);
            if (index >= 0)
            {
                return index + 1;
            }

            _strings.Add(value);
            return _strings.Count;
        }

        public byte[] BuildPoolStream(bool longStringRefs) => BuildStream(writer =>
        {
            writer.Write((uint)Utf8Codepage | (longStringRefs ? 0x8000_0000u : 0u));
            foreach (string value in _strings)
            {
                int length = Encoding.UTF8.GetByteCount(value);
                if (length > ushort.MaxValue)
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((uint)length);
                }
                else
                {
                    writer.Write((ushort)length);
                    writer.Write((ushort)1);
                }
            }
        });

        public byte[] BuildDataStream() => BuildStream(writer =>
        {
            foreach (string value in _strings)
            {
                writer.Write(Encoding.UTF8.GetBytes(value));
            }
        });
    }
}
