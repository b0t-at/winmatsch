using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Json.Schema;
using WinMatsch.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Validation;

/// <summary>Validates manifest YAML against the four bundled WinGet 1.12 Draft 7 schemas.</summary>
public static class ManifestSchemaValidator
{
    public const string SchemaVersion = "1.12.0";

    private const string Draft7Identifier = "http://json-schema.org/draft-07/schema#";

    private static readonly Dictionary<ManifestType, SchemaEntry> _schemas = LoadSchemas();

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
            return new ValidationReport(
            [
                Error("VLD1001", $"Invalid YAML: {exception.Message}", document.RepositoryPath),
            ]);
        }
        catch (JsonException exception)
        {
            return new ValidationReport(
            [
                Error("VLD1001", $"Invalid YAML scalar value: {exception.Message}", document.RepositoryPath),
            ]);
        }
        catch (InvalidDataException exception)
        {
            return new ValidationReport(
            [
                Error("VLD1001", exception.Message, document.RepositoryPath),
            ]);
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
            WriteNode(writer, root);
        }

        return (JsonDocument.Parse(output.ToArray()), root);
    }

    private static void WriteNode(Utf8JsonWriter writer, YamlNode node)
    {
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
                    WriteNode(writer, valueNode);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (YamlNode item in sequence.Children)
                {
                    WriteNode(writer, item);
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
        if (scalar.Style != ScalarStyle.Plain)
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
        else if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            writer.WriteNumberValue(integer);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

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

    private sealed record SchemaEntry(
        JsonSchema Schema,
        IReadOnlyDictionary<string, string> CanonicalProperties);
}
