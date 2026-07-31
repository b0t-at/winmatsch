using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using WinMatsch.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Validation;

/// <summary>Validates manifest YAML against the four bundled WinGet 1.12 Draft 7 schemas.</summary>
public static partial class ManifestSchemaValidator
{
    public const string SchemaVersion = "1.12.0";

    private const string Draft7Identifier = "http://json-schema.org/draft-07/schema#";
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const int MaxYamlDepth = 64;
    private const int MaxYamlNodes = 100_000;

    private static readonly Dictionary<ManifestType, SchemaEntry> _schemas = LoadSchemas();

    internal static ValidationReport ReadHeader(
        ManifestDocument document,
        out ManifestHeader? header)
    {
        ArgumentNullException.ThrowIfNull(document);
        header = null;
        JsonDocument instance;
        try
        {
            (instance, _) = ConvertYamlToJson(document.Content);
        }
        catch (YamlException exception)
        {
            return YamlFailure($"Invalid YAML: {exception.Message}", document.RepositoryPath);
        }
        catch (JsonException exception)
        {
            return YamlFailure(
                $"Invalid YAML scalar value: {exception.Message}",
                document.RepositoryPath);
        }
        catch (InvalidDataException exception)
        {
            return YamlFailure(exception.Message, document.RepositoryPath);
        }
        catch (ArgumentException exception)
        {
            return YamlFailure($"Invalid YAML: {exception.Message}", document.RepositoryPath);
        }

        using (instance)
        {
            JsonElement root = instance.RootElement;
            header = new ManifestHeader
            {
                PackageIdentifier = GetString(root, "PackageIdentifier"),
                PackageVersion = GetString(root, "PackageVersion"),
                ManifestType = GetString(root, "ManifestType"),
                ManifestVersion = GetString(root, "ManifestVersion"),
            };
        }

        return new ValidationReport();
    }

    public static ValidationReport Validate(ManifestDocument document, ManifestType manifestType)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_schemas.TryGetValue(manifestType, out SchemaEntry? entry))
        {
            return new ValidationReport(
            [
                Error(
                    "VLD1000",
                    $"Manifest type '{manifestType}' has no bundled {SchemaVersion} schema.",
                    document.RepositoryPath),
            ]);
        }

        var findings = new List<ValidationFinding>();
        JsonDocument instance;
        YamlMappingNode root;
        try
        {
            (instance, root) = ConvertYamlToJson(document.Content);
        }
        catch (YamlException exception)
        {
            return YamlFailure($"Invalid YAML: {exception.Message}", document.RepositoryPath);
        }
        catch (JsonException exception)
        {
            return YamlFailure(
                $"Invalid YAML scalar value: {exception.Message}",
                document.RepositoryPath);
        }
        catch (InvalidDataException exception)
        {
            return YamlFailure(exception.Message, document.RepositoryPath);
        }
        catch (ArgumentException exception)
        {
            return YamlFailure($"Invalid YAML: {exception.Message}", document.RepositoryPath);
        }

        using (instance)
        {
            ValidatePropertyCasing(root, entry.CanonicalProperties, document.RepositoryPath, findings);
            EvaluationResults results = entry.Schema.Evaluate(
                instance.RootElement,
                new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                    Culture = CultureInfo.InvariantCulture,
                });

            if (!results.IsValid)
            {
                AddSchemaErrors(results, document.RepositoryPath, findings);
            }
        }

        return new ValidationReport(findings);
    }

    private static Dictionary<ManifestType, SchemaEntry> LoadSchemas()
    {
        var schemas = new Dictionary<ManifestType, SchemaEntry>
        {
            [ManifestType.Version] = LoadSchema("manifest.version.1.12.0.json"),
            [ManifestType.Installer] = LoadSchema("manifest.installer.1.12.0.json"),
            [ManifestType.DefaultLocale] = LoadSchema("manifest.defaultLocale.1.12.0.json"),
            [ManifestType.Locale] = LoadSchema("manifest.locale.1.12.0.json"),
        };
        return schemas;
    }

    private static SchemaEntry LoadSchema(string fileName)
    {
        Assembly assembly = typeof(ManifestSchemaValidator).Assembly;
        string resourceName = $"WinMatsch.Validation.Schemas.{fileName}";
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled manifest schema '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();

        using JsonDocument schemaDocument = JsonDocument.Parse(text);
        string? dialect = schemaDocument.RootElement.GetProperty("$schema").GetString();
        if (!string.Equals(dialect, Draft7Identifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Bundled schema '{fileName}' must be pinned to JSON Schema Draft 7.");
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectCanonicalProperties(schemaDocument.RootElement, properties);
        var buildOptions = new BuildOptions
        {
            Dialect = Dialect.Draft07,
            SchemaRegistry = new SchemaRegistry(),
            VocabularyRegistry = new VocabularyRegistry(),
            DialectRegistry = new DialectRegistry(),
        };
        JsonSchema schema = JsonSchema.FromText(text, buildOptions);
        return new SchemaEntry(schema, properties);
    }

    private static void CollectCanonicalProperties(
        JsonElement element,
        Dictionary<string, string> properties)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty declaredProperty in property.Value.EnumerateObject())
                    {
                        properties.TryAdd(declaredProperty.Name, declaredProperty.Name);
                    }
                }

                CollectCanonicalProperties(property.Value, properties);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                CollectCanonicalProperties(child, properties);
            }
        }
    }

    private static (JsonDocument Document, YamlMappingNode Root) ConvertYamlToJson(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"A manifest cannot exceed {MaxManifestBytes} UTF-8 bytes.");
        }

        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("A manifest must contain exactly one YAML mapping document.");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            int nodesRemaining = MaxYamlNodes;
            WriteNode(writer, root, depth: 0, ref nodesRemaining);
        }

        return (JsonDocument.Parse(output.ToArray()), root);
    }

    private static void WriteNode(
        Utf8JsonWriter writer,
        YamlNode node,
        int depth,
        ref int nodesRemaining)
    {
        if (!node.Anchor.IsEmpty)
        {
            throw new InvalidDataException(
                "YAML anchors and aliases are not permitted in manifests.");
        }

        if (depth > MaxYamlDepth)
        {
            throw new InvalidDataException(
                $"YAML nesting cannot exceed {MaxYamlDepth} levels.");
        }

        nodesRemaining--;
        if (nodesRemaining < 0)
        {
            throw new InvalidDataException(
                $"A manifest cannot contain more than {MaxYamlNodes} YAML nodes.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: not null } key)
                    {
                        throw new InvalidDataException("Manifest mapping keys must be non-null strings.");
                    }

                    writer.WritePropertyName(key.Value);
                    WriteNode(writer, valueNode, depth + 1, ref nodesRemaining);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (YamlNode item in sequence.Children)
                {
                    WriteNode(writer, item, depth + 1, ref nodesRemaining);
                }

                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
            default:
                throw new InvalidDataException($"Unsupported YAML node type '{node.NodeType}'.");
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        string? value = scalar.Value;
        if (!scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific)
        {
            WriteExplicitlyTaggedScalar(writer, scalar.Tag.Value, value);
            return;
        }

        if (scalar.Tag == "!" || scalar.Style != ScalarStyle.Plain)
        {
            writer.WriteStringValue(value);
            return;
        }

        if (value is null
            || value.Length == 0
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("~", StringComparison.Ordinal))
        {
            writer.WriteNullValue();
        }
        else if (bool.TryParse(value, out bool boolean))
        {
            writer.WriteBooleanValue(boolean);
        }
        else if (TryGetYamlIntegerJson(value, out string? integer))
        {
            writer.WriteRawValue(integer);
        }
        else if (IsNonFiniteYamlNumber(value))
        {
            throw new InvalidDataException(
                $"YAML numeric scalar '{value}' cannot be represented as a JSON number.");
        }
        else if (TryGetYamlFloatJson(value, out string? number))
        {
            writer.WriteRawValue(number);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteExplicitlyTaggedScalar(
        Utf8JsonWriter writer,
        string tag,
        string? value)
    {
        if (tag == "tag:yaml.org,2002:str")
        {
            writer.WriteStringValue(value);
            return;
        }

        if (tag == "tag:yaml.org,2002:null"
            && (value is null
                || value.Length == 0
                || value.Equals("null", StringComparison.OrdinalIgnoreCase)
                || value.Equals("~", StringComparison.Ordinal)))
        {
            writer.WriteNullValue();
            return;
        }

        if (tag == "tag:yaml.org,2002:bool"
            && bool.TryParse(value, out bool boolean))
        {
            writer.WriteBooleanValue(boolean);
            return;
        }

        if (tag == "tag:yaml.org,2002:int"
            && value is not null
            && TryGetYamlIntegerJson(value, out string? integer))
        {
            writer.WriteRawValue(integer);
            return;
        }

        if (tag == "tag:yaml.org,2002:float"
            && value is not null
            && !IsNonFiniteYamlNumber(value)
            && TryGetYamlFloatJson(value, out string? number))
        {
            writer.WriteRawValue(number);
            return;
        }

        throw new InvalidDataException(
            $"YAML scalar tag '{tag}' is unsupported or has an invalid value.");
    }

    private static bool TryGetYamlIntegerJson(string value, out string jsonNumber)
    {
        int sign = 1;
        int prefixIndex = 0;
        if (value.StartsWith('+'))
        {
            prefixIndex = 1;
        }
        else if (value.StartsWith('-'))
        {
            sign = -1;
            prefixIndex = 1;
        }

        ReadOnlySpan<char> unsigned = value.AsSpan(prefixIndex);
        int radix;
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            radix = 16;
            unsigned = unsigned[2..];
        }
        else if (unsigned.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            radix = 8;
            unsigned = unsigned[2..];
        }
        else if (unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            radix = 2;
            unsigned = unsigned[2..];
        }
        else
        {
            radix = 10;
        }

        if (unsigned.IsEmpty || !HasValidDigitSeparators(unsigned))
        {
            jsonNumber = string.Empty;
            return false;
        }

        BigInteger parsed = BigInteger.Zero;
        foreach (char character in unsigned)
        {
            if (character == '_')
            {
                continue;
            }

            int digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix)
            {
                jsonNumber = string.Empty;
                return false;
            }

            parsed = (parsed * radix) + digit;
        }

        parsed *= sign;
        jsonNumber = parsed.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool HasValidDigitSeparators(ReadOnlySpan<char> value)
    {
        if (value[0] == '_' || value[^1] == '_')
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] == '_' && value[index - 1] == '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetYamlFloatJson(string value, out string jsonNumber)
    {
        if (value.IndexOfAny(['.', 'e', 'E']) < 0 || !YamlFloatPattern().IsMatch(value))
        {
            jsonNumber = string.Empty;
            return false;
        }

        string normalized = value.Replace("_", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith('+'))
        {
            normalized = normalized[1..];
        }

        int exponentIndex = normalized.IndexOfAny(['e', 'E']);
        int mantissaEnd = exponentIndex < 0 ? normalized.Length : exponentIndex;
        if (normalized[0] == '.')
        {
            normalized = $"0{normalized}";
            mantissaEnd++;
        }
        else if (normalized.StartsWith("-.", StringComparison.Ordinal))
        {
            normalized = $"-0{normalized[1..]}";
            mantissaEnd++;
        }

        if (normalized[mantissaEnd - 1] == '.')
        {
            normalized = normalized.Insert(mantissaEnd, "0");
        }

        jsonNumber = normalized;
        return true;
    }

    private static bool IsNonFiniteYamlNumber(string value)
        => value.Equals(".inf", StringComparison.OrdinalIgnoreCase)
            || value.Equals("+.inf", StringComparison.OrdinalIgnoreCase)
            || value.Equals("-.inf", StringComparison.OrdinalIgnoreCase)
            || value.Equals(".nan", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"^[+-]?(?:(?:[0-9](?:_?[0-9])*)(?:\.(?:[0-9](?:_?[0-9])*)?)?|\.(?:[0-9](?:_?[0-9])*))(?:[eE][+-]?[0-9](?:_?[0-9])*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex YamlFloatPattern();

    private static void ValidatePropertyCasing(
        YamlNode node,
        IReadOnlyDictionary<string, string> canonicalProperties,
        string documentPath,
        List<ValidationFinding> findings,
        string yamlPath = "$")
    {
        if (node is YamlMappingNode mapping)
        {
            foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: not null } key)
                {
                    continue;
                }

                string propertyName = key.Value;
                string propertyPath = $"{yamlPath}.{propertyName}";
                if (canonicalProperties.TryGetValue(propertyName, out string? canonical)
                    && !string.Equals(propertyName, canonical, StringComparison.Ordinal))
                {
                    findings.Add(Error(
                        "VLD1002",
                        $"Property '{propertyName}' must use exact schema casing '{canonical}'.",
                        $"{documentPath}:{propertyPath}"));
                }

                ValidatePropertyCasing(valueNode, canonicalProperties, documentPath, findings, propertyPath);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            for (int i = 0; i < sequence.Children.Count; i++)
            {
                ValidatePropertyCasing(
                    sequence.Children[i],
                    canonicalProperties,
                    documentPath,
                    findings,
                    $"{yamlPath}[{i}]");
            }
        }
    }

    private static void AddSchemaErrors(
        EvaluationResults results,
        string documentPath,
        List<ValidationFinding> findings)
    {
        IEnumerable<EvaluationResults> details = results.Details ?? [results];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (EvaluationResults detail in details)
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach ((string keyword, string message) in detail.Errors.OrderBy(static error => error.Key, StringComparer.Ordinal))
            {
                string instancePath = detail.InstanceLocation.ToString();
                string key = $"{instancePath}\0{keyword}\0{message}";
                if (!seen.Add(key))
                {
                    continue;
                }

                findings.Add(Error(
                    "VLD1003",
                    $"Schema keyword '{keyword}' failed: {message}",
                    $"{documentPath}{instancePath}"));
            }
        }
    }

    private static ValidationFinding Error(string code, string message, string path)
        => new(code, ValidationSeverity.Error, message, path);

    private static ValidationReport YamlFailure(string message, string path)
        => new([Error("VLD1001", message, path)]);

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private sealed record SchemaEntry(
        JsonSchema Schema,
        IReadOnlyDictionary<string, string> CanonicalProperties);
}
