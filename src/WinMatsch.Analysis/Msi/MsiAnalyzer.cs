using System.Globalization;
using OpenMcdf;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Msi;

/// <summary>
/// Analyzes Windows Installer packages (.msi). The database is a Compound File Binary
/// container (read with OpenMcdf) whose table streams carry the <c>Property</c> table —
/// ProductCode, ProductName, ProductVersion, Manufacturer, UpgradeCode, ProductLanguage,
/// ALLUSERS — while the <c>\x05SummaryInformation</c> stream reveals the target architecture
/// (Template) and the authoring tool (Creating Application, used for WiX detection).
/// </summary>
public sealed class MsiAnalyzer : IInstallerAnalyzer
{
    private const string StringPoolStreamName = "_StringPool";
    private const string StringDataStreamName = "_StringData";
    private const string TablesStreamName = "_Tables";
    private const string ColumnsStreamName = "_Columns";
    private const string PropertyTableName = "Property";
    private const string SummaryInformationStreamName = "\u0005SummaryInformation";

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.Equals(Path.GetExtension(fileName), ".msi", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MsiStreams streams = ReadStreams(stream, fileName);

        if (streams.StringPool is null || streams.StringData is null || streams.Tables is null || streams.Columns is null)
        {
            throw new InvalidDataException(
                $"'{fileName}' is not a valid MSI database: the _StringPool, _StringData, _Tables or _Columns stream is missing.");
        }

        MsiStringPool pool = MsiStringPool.Read(streams.StringPool, streams.StringData);
        if (!MsiTableReader.ReadTableNames(pool, streams.Tables).Contains(PropertyTableName))
        {
            throw new InvalidDataException($"'{fileName}' is not a valid MSI database: it has no Property table.");
        }

        Dictionary<string, string> properties = ReadProperties(pool, streams.Columns, streams.PropertyTable);
        MsiSummaryInformation summary = streams.SummaryInformation is null
            ? MsiSummaryInformation.Empty
            : MsiSummaryInformation.Read(streams.SummaryInformation);

        string? productCode = NullIfEmpty(properties.GetValueOrDefault("ProductCode"));
        string? productName = NullIfEmpty(properties.GetValueOrDefault("ProductName"));
        string? productVersion = NullIfEmpty(properties.GetValueOrDefault("ProductVersion"));
        string? manufacturer = NullIfEmpty(properties.GetValueOrDefault("Manufacturer"));
        string? upgradeCode = NullIfEmpty(properties.GetValueOrDefault("UpgradeCode"));

        var installer = new Installer
        {
            Architecture = MapArchitecture(summary.Template),
            InstallerType = IsWix(summary.CreatingApplication, properties) ? InstallerType.Wix : InstallerType.Msi,
            Scope = MapScope(properties.GetValueOrDefault("ALLUSERS")),
            ProductCode = productCode,
            InstallerLocale = MapLanguage(properties.GetValueOrDefault("ProductLanguage")),
        };

        if (productName is not null || manufacturer is not null || productVersion is not null
            || productCode is not null || upgradeCode is not null)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = productName,
                    Publisher = manufacturer,
                    DisplayVersion = productVersion,
                    ProductCode = productCode,
                    UpgradeCode = upgradeCode,
                },
            ];
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Msi,
            Installers = [installer],
            ProductName = productName,
            Publisher = manufacturer,
            ProductVersion = productVersion,
        };
    }

    /// <summary>Loads the raw bytes of the few streams the analyzer consumes.</summary>
    private static MsiStreams ReadStreams(Stream stream, string fileName)
    {
        var streams = new MsiStreams();
        try
        {
            using var root = RootStorage.Open(stream, StorageModeFlags.LeaveOpen);
            foreach (EntryInfo entry in root.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream)
                {
                    continue;
                }

                string decoded = MsiStreamName.Decode(entry.Name, out bool isTable);
                if (isTable)
                {
                    switch (decoded)
                    {
                        case StringPoolStreamName:
                            streams.StringPool = ReadStreamBytes(root, entry);
                            break;
                        case StringDataStreamName:
                            streams.StringData = ReadStreamBytes(root, entry);
                            break;
                        case TablesStreamName:
                            streams.Tables = ReadStreamBytes(root, entry);
                            break;
                        case ColumnsStreamName:
                            streams.Columns = ReadStreamBytes(root, entry);
                            break;
                        case PropertyTableName:
                            streams.PropertyTable = ReadStreamBytes(root, entry);
                            break;
                        default:
                            break; // Not a table the analyzer consumes.
                    }
                }
                else if (decoded == SummaryInformationStreamName)
                {
                    streams.SummaryInformation = ReadStreamBytes(root, entry);
                }
            }
        }
        catch (FileFormatException exception)
        {
            throw new InvalidDataException($"'{fileName}' is not a valid MSI database: {exception.Message}", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"'{fileName}' is not a valid MSI database: the file is truncated.", exception);
        }

        return streams;
    }

    private static byte[] ReadStreamBytes(RootStorage root, EntryInfo entry)
    {
        using CfbStream stream = root.OpenStream(entry.Name);
        return AnalysisLimits.ReadBounded(
            stream,
            entry.Length,
            $"MSI stream '{entry.Name}'",
            AnalysisLimits.MaxMsiStreamBytes);
    }

    /// <summary>Reads the Property table into a name → value map; an absent stream means an empty table.</summary>
    private static Dictionary<string, string> ReadProperties(MsiStringPool pool, byte[] columnsStream, byte[]? propertyStream)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (propertyStream is null)
        {
            return properties;
        }

        List<MsiColumn> columns = MsiTableReader.ReadColumns(pool, columnsStream, PropertyTableName);
        if (columns.Count < 2)
        {
            throw new InvalidDataException("The MSI Property table does not declare the expected Property and Value columns.");
        }

        foreach (MsiCell[] row in MsiTableReader.ReadRows(pool, propertyStream, columns))
        {
            if (row[0].Text is { Length: > 0 } name)
            {
                properties[name] = row[1].Text ?? string.Empty;
            }
        }

        return properties;
    }

    /// <summary>
    /// Maps the platform prefix of the SummaryInformation Template (for example
    /// <c>x64;1033</c>) to an architecture. An empty prefix — and, defensively, a missing
    /// SummaryInformation stream — means a 32-bit package, matching Windows Installer's default.
    /// </summary>
    /// <exception cref="InvalidDataException">The platform token is not a known value.</exception>
    private static Architecture MapArchitecture(string? template)
    {
        string platform = template ?? string.Empty;
        int separator = platform.IndexOf(';', StringComparison.Ordinal);
        if (separator >= 0)
        {
            platform = platform[..separator];
        }

        platform = platform.Trim();
        if (platform.Length == 0 || platform.Equals("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.X86;
        }

        if (platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
            || platform.Equals("Intel64", StringComparison.OrdinalIgnoreCase)
            || platform.Equals("AMD64", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.X64;
        }

        if (platform.Equals("Arm64", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.Arm64;
        }

        if (platform.Equals("Arm", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.Arm;
        }

        throw new InvalidDataException($"The MSI SummaryInformation Template declares an unknown platform '{platform}'.");
    }

    /// <summary>
    /// A package is WiX-authored when the SummaryInformation Creating Application names WiX,
    /// or any Property table name or value mentions it (WiX extensions leave properties like
    /// <c>WixUI_Mode</c> behind even when the creating application is rewritten).
    /// </summary>
    private static bool IsWix(string? creatingApplication, Dictionary<string, string> properties)
    {
        if (creatingApplication is not null
            && (creatingApplication.Contains("wix", StringComparison.OrdinalIgnoreCase)
                || creatingApplication.Contains("windows installer xml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach ((string name, string value) in properties)
        {
            if (name.Contains("wix", StringComparison.OrdinalIgnoreCase)
                || value.Contains("wix", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps the ALLUSERS property to an installation scope: "1" is a per-machine install;
    /// absent or empty means Windows Installer's per-user default; "2" chooses at install
    /// time based on the user's privileges, so no scope is claimed — and the same applies,
    /// defensively, to any other value.
    /// </summary>
    private static Scope? MapScope(string? allUsers) => allUsers switch
    {
        "1" => Scope.Machine,
        null or "" => Scope.User,
        _ => null,
    };

    /// <summary>ProductLanguage is an LCID; see <see cref="Lcid"/> for the mapping caveats.</summary>
    private static LanguageTag? MapLanguage(string? productLanguage)
        => productLanguage is not null
            && int.TryParse(productLanguage, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lcid)
            ? Lcid.ToLanguageTag(lcid)
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The raw contents of the streams the analyzer reads from the compound file.</summary>
    private sealed class MsiStreams
    {
        public byte[]? StringPool { get; set; }

        public byte[]? StringData { get; set; }

        public byte[]? Tables { get; set; }

        public byte[]? Columns { get; set; }

        public byte[]? PropertyTable { get; set; }

        public byte[]? SummaryInformation { get; set; }
    }
}
