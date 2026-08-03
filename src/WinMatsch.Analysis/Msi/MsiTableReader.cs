using System.Buffers.Binary;

namespace WinMatsch.Analysis.Msi;

/// <summary>One cell of an MSI table row: a pool string for string columns, a number for integer columns.</summary>
internal readonly record struct MsiCell(string? Text, int? Number);

/// <summary>The schema of one MSI table column, read from the <c>_Columns</c> catalog table.</summary>
internal sealed class MsiColumn
{
    private const ushort StringBit = 0x0800;
    private const int WidthMask = 0xFF;

    /// <summary>The 1-based position of the column within its table.</summary>
    public required int Number { get; init; }

    /// <summary>The column name.</summary>
    public required string Name { get; init; }

    /// <summary>The raw column type bit field (string flag, nullability, key flag, byte width).</summary>
    public required ushort TypeBits { get; init; }

    /// <summary>Whether cells of this column are string pool references rather than integers.</summary>
    public bool IsString => (TypeBits & StringBit) != 0;

    /// <summary>
    /// The serialized cell width in bytes: string references follow the pool's reference
    /// width; integers occupy four bytes when the declared width is 4, otherwise two.
    /// </summary>
    public int Width(bool longStringRefs)
        => IsString ? (longStringRefs ? 3 : 2) : ((TypeBits & WidthMask) == 4 ? 4 : 2);
}

/// <summary>
/// Reads MSI database tables. Table streams are column-major: all cells of the first column
/// come first, then all cells of the second, and so on; the row count is the stream size
/// divided by the summed cell widths. Integer cells are stored biased (the actual value plus
/// 0x8000 for two-byte cells or 0x80000000 for four-byte cells, modulo the cell size) and a
/// stored zero means null.
/// </summary>
internal static class MsiTableReader
{
    // The fixed schema of the _Columns catalog table itself: table name (string),
    // column number (int16), column name (string), column type (int16).
    private static readonly MsiColumn[] _columnsCatalogSchema =
    [
        new MsiColumn { Number = 1, Name = "Table", TypeBits = 0x0800 },
        new MsiColumn { Number = 2, Name = "Number", TypeBits = 0x0002 },
        new MsiColumn { Number = 3, Name = "Name", TypeBits = 0x0800 },
        new MsiColumn { Number = 4, Name = "Type", TypeBits = 0x0002 },
    ];

    // The _Tables catalog is a single string column listing the table names.
    private static readonly MsiColumn[] _tablesCatalogSchema =
    [
        new MsiColumn { Number = 1, Name = "Name", TypeBits = 0x0800 },
    ];

    /// <summary>Reads the table names listed in the <c>_Tables</c> catalog stream.</summary>
    public static List<string> ReadTableNames(MsiStringPool pool, ReadOnlySpan<byte> tablesStream)
    {
        List<string> names = [];
        foreach (MsiCell[] row in ReadRows(pool, tablesStream, _tablesCatalogSchema))
        {
            if (row[0].Text is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Reads the column schema of one table from the <c>_Columns</c> catalog stream,
    /// ordered by column number.
    /// </summary>
    public static List<MsiColumn> ReadColumns(MsiStringPool pool, ReadOnlySpan<byte> columnsStream, string tableName)
    {
        List<MsiColumn> columns = [];
        foreach (MsiCell[] row in ReadRows(pool, columnsStream, _columnsCatalogSchema))
        {
            if (!string.Equals(row[0].Text, tableName, StringComparison.Ordinal))
            {
                continue;
            }

            if (row[1].Number is not { } number || row[2].Text is not { Length: > 0 } name || row[3].Number is not { } type)
            {
                throw new InvalidDataException(
                    $"The MSI _Columns catalog contains an incomplete definition for table '{tableName}'.");
            }

            columns.Add(new MsiColumn { Number = number, Name = name, TypeBits = unchecked((ushort)type) });
        }

        columns.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        return columns;
    }

    /// <summary>Reads all rows of a column-major table stream using the given column schema.</summary>
    /// <exception cref="InvalidDataException">The stream size is not a whole number of rows.</exception>
    public static List<MsiCell[]> ReadRows(MsiStringPool pool, ReadOnlySpan<byte> tableStream, IReadOnlyList<MsiColumn> columns)
    {
        int rowWidth = 0;
        foreach (MsiColumn column in columns)
        {
            rowWidth += column.Width(pool.LongStringRefs);
        }

        if (rowWidth == 0)
        {
            return [];
        }

        (int rowCount, int remainder) = Math.DivRem(tableStream.Length, rowWidth);
        if (remainder != 0)
        {
            throw new InvalidDataException(
                $"An MSI table stream of {tableStream.Length} bytes is not a whole number of {rowWidth}-byte rows.");
        }

        var rows = new List<MsiCell[]>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            rows.Add(new MsiCell[columns.Count]);
        }

        int offset = 0;
        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            MsiColumn column = columns[columnIndex];
            int width = column.Width(pool.LongStringRefs);
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                rows[rowIndex][columnIndex] = ReadCell(pool, tableStream, offset, column, width);
                offset += width;
            }
        }

        return rows;
    }

    private static MsiCell ReadCell(MsiStringPool pool, ReadOnlySpan<byte> stream, int offset, MsiColumn column, int width)
    {
        if (column.IsString)
        {
            return new MsiCell(pool.Get(pool.ReadStringRef(stream, offset)), null);
        }

        if (width == 4)
        {
            uint stored32 = BinaryPrimitives.ReadUInt32LittleEndian(stream[offset..]);
            return stored32 == 0 ? default : new MsiCell(null, unchecked((int)(stored32 ^ 0x8000_0000u)));
        }

        ushort stored16 = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
        return stored16 == 0 ? default : new MsiCell(null, stored16 - 0x8000);
    }
}
