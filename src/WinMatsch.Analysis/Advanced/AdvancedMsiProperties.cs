using OpenMcdf;
using WinMatsch.Analysis.Msi;

namespace WinMatsch.Analysis.Advanced;

/// <summary>Reads the MSI property needed to decide whether its ARP identity is intentionally hidden.</summary>
internal static class AdvancedMsiProperties
{
    public static bool IsArpSystemComponent(Stream stream)
    {
        byte[]? stringPool = null;
        byte[]? stringData = null;
        byte[]? columns = null;
        byte[]? property = null;

        using (var root = RootStorage.Open(stream, StorageModeFlags.LeaveOpen))
        {
            foreach (EntryInfo entry in root.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream)
                {
                    continue;
                }

                string name = MsiStreamName.Decode(entry.Name, out bool isTable);
                if (!isTable)
                {
                    continue;
                }

                switch (name)
                {
                    case "_StringPool":
                        stringPool = Read(root, entry);
                        break;
                    case "_StringData":
                        stringData = Read(root, entry);
                        break;
                    case "_Columns":
                        columns = Read(root, entry);
                        break;
                    case "Property":
                        property = Read(root, entry);
                        break;
                }
            }
        }

        if (stringPool is null || stringData is null || columns is null || property is null)
        {
            return false;
        }

        MsiStringPool pool = MsiStringPool.Read(stringPool, stringData);
        List<MsiColumn> schema = MsiTableReader.ReadColumns(pool, columns, "Property");
        foreach (MsiCell[] row in MsiTableReader.ReadRows(pool, property, schema))
        {
            if (row.Length >= 2
                && string.Equals(row[0].Text, "ARPSYSTEMCOMPONENT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(row[1].Text?.Trim(), "1", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Read(RootStorage root, EntryInfo entry)
    {
        using CfbStream source = root.OpenStream(entry.Name);
        return AnalysisLimits.ReadBounded(
            source,
            entry.Length,
            $"Advanced Installer MSI stream '{entry.Name}'",
            AnalysisLimits.MaxMsiStreamBytes);
    }
}
