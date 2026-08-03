using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinMatsch.Validation.Schema;

internal sealed class Draft7SchemaCompiler
{
    internal const string Draft7Identifier = "http://json-schema.org/draft-07/schema#";

    internal static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly JsonElement _root;
    private readonly string _sourceName;
    private readonly Dictionary<string, Draft7Schema> _compiled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
    private readonly List<CompilationFrame> _stack = [];

    private Draft7SchemaCompiler(JsonElement root, string sourceName)
    {
        _root = root;
        _sourceName = sourceName;
    }

    internal static Draft7Schema Compile(string schemaJson, string sourceName = "<schema>")
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                schemaJson,
                new JsonDocumentOptions { MaxDepth = 128 });
            return new Draft7SchemaCompiler(document.RootElement, sourceName).CompileNode(
                document.RootElement,
                string.Empty,
                isRoot: true);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Schema '{sourceName}' is not valid JSON: {exception.Message}",
                exception);
        }
    }

    private Draft7Schema CompileNode(
        JsonElement element,
        string pointer,
        bool isRoot = false,
        bool advancesInstance = false)
    {
        if (_compiled.TryGetValue(pointer, out Draft7Schema? compiled))
        {
            return compiled;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Failure(pointer, "<schema>", "boolean and non-object schemas are not supported.");
        }

        ValidateKeywords(element, pointer, isRoot);
        var schema = new Draft7Schema();
        _compiled.Add(pointer, schema);
        _visiting.Add(pointer);
        _stack.Add(new CompilationFrame(pointer, advancesInstance));

        try
        {
            CompileDefinitions(element, pointer, isRoot);

            if (element.TryGetProperty("$ref", out JsonElement referenceElement))
            {
                ValidateReferenceSiblings(element, pointer, isRoot);
                string reference = ReadString(referenceElement, pointer, "$ref");
                (JsonElement target, string targetPointer) = ResolveReference(reference, pointer);
                if (_visiting.Contains(targetPointer))
                {
                    EnsureRecursiveReferenceAdvancesInstance(pointer, targetPointer, reference);
                }

                schema.Reference = CompileNode(target, targetPointer);
                return schema;
            }

            schema.Types = CompileTypes(element, pointer, out IReadOnlyList<string> typeNames);
            schema.TypeNames = typeNames;
            schema.EnumValues = CompileEnum(element, pointer);
            schema.Constant = CompileConstant(element);
            schema.RequiredProperties = CompileRequired(element, pointer);
            schema.Properties = CompileProperties(element, pointer);
            schema.Items = CompileSingleSchema(
                element,
                pointer,
                "items",
                advancesInstance: true);
            schema.MinimumLength = CompileCount(element, pointer, "minLength");
            schema.MaximumLength = CompileCount(element, pointer, "maxLength");
            schema.Pattern = CompilePattern(element, pointer);
            schema.MinimumItems = CompileCount(element, pointer, "minItems");
            schema.MaximumItems = CompileCount(element, pointer, "maxItems");
            schema.UniqueItems = CompileBoolean(element, pointer, "uniqueItems");
            schema.Minimum = CompileNumber(element, pointer, "minimum");
            schema.Maximum = CompileNumber(element, pointer, "maximum");
            schema.OneOf = CompileOneOf(element, pointer);
            schema.Not = CompileSingleSchema(
                element,
                pointer,
                "not",
                advancesInstance: false);
            return schema;
        }
        catch
        {
            _compiled.Remove(pointer);
            throw;
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
            _visiting.Remove(pointer);
        }
    }

    private void ValidateKeywords(JsonElement element, string pointer, bool isRoot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Failure(pointer, property.Name, "duplicate schema keywords are not allowed.");
            }

            if (!IsAllowedKeyword(property.Name))
            {
                throw Failure(pointer, property.Name, "keyword is outside the supported Draft-07 subset.");
            }

            if (!isRoot && property.Name is "$schema" or "$id" or "definitions")
            {
                throw Failure(pointer, property.Name, "keyword is supported at the schema root only.");
            }
        }

        ValidateOptionalString(element, pointer, "title");
        ValidateOptionalString(element, pointer, "description");
        ValidateOptionalString(element, pointer, "format");
        ValidateOptionalString(element, pointer, "$id");
        if (element.TryGetProperty("$schema", out JsonElement dialect))
        {
            string identifier = ReadString(dialect, pointer, "$schema");
            if (!string.Equals(identifier, Draft7Identifier, StringComparison.Ordinal))
            {
                throw Failure(pointer, "$schema", $"only '{Draft7Identifier}' is supported.");
            }
        }
    }

    private void CompileDefinitions(JsonElement element, string pointer, bool isRoot)
    {
        if (!element.TryGetProperty("definitions", out JsonElement definitions))
        {
            return;
        }

        if (!isRoot || definitions.ValueKind != JsonValueKind.Object)
        {
            throw Failure(pointer, "definitions", "value must be an object at the schema root.");
        }

        EnsureUniqueMembers(definitions, pointer, "definitions");
        foreach (JsonProperty definition in definitions.EnumerateObject())
        {
            string definitionPointer = AppendPointer(AppendPointer(pointer, "definitions"), definition.Name);
            CompileNode(definition.Value, definitionPointer);
        }
    }

    private Dictionary<string, Draft7Schema> CompileProperties(
        JsonElement element,
        string pointer)
    {
        if (!element.TryGetProperty("properties", out JsonElement properties))
        {
            return new Dictionary<string, Draft7Schema>(StringComparer.Ordinal);
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            throw Failure(pointer, "properties", "value must be an object.");
        }

        EnsureUniqueMembers(properties, pointer, "properties");
        var compiled = new Dictionary<string, Draft7Schema>(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            string childPointer = AppendPointer(AppendPointer(pointer, "properties"), property.Name);
            compiled.Add(
                property.Name,
                CompileNode(property.Value, childPointer, advancesInstance: true));
        }

        return compiled;
    }

    private Draft7Schema? CompileSingleSchema(
        JsonElement element,
        string pointer,
        string keyword,
        bool advancesInstance)
    {
        if (!element.TryGetProperty(keyword, out JsonElement child))
        {
            return null;
        }

        if (child.ValueKind == JsonValueKind.Array && keyword == "items")
        {
            throw Failure(pointer, keyword, "tuple-form 'items' arrays are not supported.");
        }

        return CompileNode(
            child,
            AppendPointer(pointer, keyword),
            advancesInstance: advancesInstance);
    }

    private List<Draft7Schema> CompileOneOf(JsonElement element, string pointer)
    {
        if (!element.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            return [];
        }

        if (oneOf.ValueKind != JsonValueKind.Array || oneOf.GetArrayLength() == 0)
        {
            throw Failure(pointer, "oneOf", "value must be a non-empty array of schemas.");
        }

        var schemas = new List<Draft7Schema>(oneOf.GetArrayLength());
        int index = 0;
        foreach (JsonElement child in oneOf.EnumerateArray())
        {
            schemas.Add(CompileNode(
                child,
                AppendPointer(
                    AppendPointer(pointer, "oneOf"),
                    index.ToString(CultureInfo.InvariantCulture))));
            index++;
        }

        return schemas;
    }

    private JsonSchemaType CompileTypes(
        JsonElement element,
        string pointer,
        out IReadOnlyList<string> typeNames)
    {
        if (!element.TryGetProperty("type", out JsonElement type))
        {
            typeNames = [];
            return JsonSchemaType.None;
        }

        var names = new List<string>();
        if (type.ValueKind == JsonValueKind.String)
        {
            names.Add(type.GetString()!);
        }
        else if (type.ValueKind == JsonValueKind.Array && type.GetArrayLength() != 0)
        {
            foreach (JsonElement item in type.EnumerateArray())
            {
                names.Add(ReadString(item, pointer, "type"));
            }
        }
        else
        {
            throw Failure(pointer, "type", "value must be a type name or a non-empty array of type names.");
        }

        JsonSchemaType types = JsonSchemaType.None;
        foreach (string name in names)
        {
            JsonSchemaType next = name switch
            {
                "null" => JsonSchemaType.Null,
                "boolean" => JsonSchemaType.Boolean,
                "object" => JsonSchemaType.Object,
                "array" => JsonSchemaType.Array,
                "number" => JsonSchemaType.Number,
                "string" => JsonSchemaType.String,
                "integer" => JsonSchemaType.Integer,
                _ => throw Failure(pointer, "type", $"'{name}' is not a Draft-07 type name."),
            };
            if ((types & next) != 0)
            {
                throw Failure(pointer, "type", $"type name '{name}' is duplicated.");
            }

            types |= next;
        }

        typeNames = names;
        return types;
    }

    private List<JsonElement> CompileEnum(JsonElement element, string pointer)
    {
        if (!element.TryGetProperty("enum", out JsonElement enumElement))
        {
            return [];
        }

        if (enumElement.ValueKind != JsonValueKind.Array || enumElement.GetArrayLength() == 0)
        {
            throw Failure(pointer, "enum", "value must be a non-empty array.");
        }

        var values = new List<JsonElement>(enumElement.GetArrayLength());
        var unique = new HashSet<JsonElement>(JsonDeepEquality.Instance);
        foreach (JsonElement value in enumElement.EnumerateArray())
        {
            JsonElement clone = value.Clone();
            if (!unique.Add(clone))
            {
                throw Failure(pointer, "enum", "values must be unique under JSON Schema equality.");
            }

            values.Add(clone);
        }

        return values;
    }

    private static JsonElement? CompileConstant(JsonElement element)
        => element.TryGetProperty("const", out JsonElement constant) ? constant.Clone() : null;

    private List<string> CompileRequired(JsonElement element, string pointer)
    {
        if (!element.TryGetProperty("required", out JsonElement required))
        {
            return [];
        }

        if (required.ValueKind != JsonValueKind.Array)
        {
            throw Failure(pointer, "required", "value must be an array of unique property names.");
        }

        var names = new List<string>(required.GetArrayLength());
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in required.EnumerateArray())
        {
            string name = ReadString(item, pointer, "required");
            if (!unique.Add(name))
            {
                throw Failure(pointer, "required", $"property name '{name}' is duplicated.");
            }

            names.Add(name);
        }

        return names;
    }

    private int? CompileCount(JsonElement element, string pointer, string keyword)
    {
        if (!element.TryGetProperty(keyword, out JsonElement count))
        {
            return null;
        }

        if (count.ValueKind != JsonValueKind.Number
            || !JsonNumber.TryParse(count.GetRawText(), out JsonNumber number)
            || !number.TryGetNonNegativeInt32(out int value))
        {
            throw Failure(pointer, keyword, "value must be a non-negative 32-bit integer.");
        }

        return value;
    }

    private bool CompileBoolean(JsonElement element, string pointer, string keyword)
    {
        if (!element.TryGetProperty(keyword, out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Failure(pointer, keyword, "value must be a boolean.");
        }

        return value.GetBoolean();
    }

    private JsonNumber? CompileNumber(JsonElement element, string pointer, string keyword)
    {
        if (!element.TryGetProperty(keyword, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !JsonNumber.TryParse(value.GetRawText(), out JsonNumber number))
        {
            throw Failure(pointer, keyword, "value must be a JSON number.");
        }

        return number;
    }

    private Regex? CompilePattern(JsonElement element, string pointer)
    {
        if (!element.TryGetProperty("pattern", out JsonElement patternElement))
        {
            return null;
        }

        string pattern = ReadString(patternElement, pointer, "pattern");
        try
        {
            return new Regex(
                pattern,
                RegexOptions.ECMAScript | RegexOptions.CultureInvariant,
                PatternMatchTimeout);
        }
        catch (ArgumentException exception)
        {
            throw Failure(pointer, "pattern", $"'{pattern}' is not a supported ECMA-262 pattern.", exception);
        }
    }

    private (JsonElement Element, string Pointer) ResolveReference(
        string reference,
        string sourcePointer)
    {
        if (!reference.StartsWith("#/definitions/", StringComparison.Ordinal)
            || reference.Contains('%', StringComparison.Ordinal))
        {
            throw Failure(
                sourcePointer,
                "$ref",
                "only unencoded internal references below '#/definitions/' are supported.");
        }

        string pointer = reference[1..];
        JsonElement current = _root;
        foreach (string encodedToken in pointer[1..].Split('/'))
        {
            string token = DecodePointerToken(encodedToken, sourcePointer);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current))
                {
                    throw Failure(sourcePointer, "$ref", $"reference '{reference}' does not resolve.");
                }
            }
            else if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                throw Failure(sourcePointer, "$ref", $"reference '{reference}' does not resolve.");
            }
        }

        return (current, pointer);
    }

    private string DecodePointerToken(string token, string sourcePointer)
    {
        if (!token.Contains('~', StringComparison.Ordinal))
        {
            return token;
        }

        var decoded = new System.Text.StringBuilder(token.Length);
        for (int index = 0; index < token.Length; index++)
        {
            if (token[index] != '~')
            {
                decoded.Append(token[index]);
                continue;
            }

            if (++index == token.Length || token[index] is not ('0' or '1'))
            {
                throw Failure(sourcePointer, "$ref", "reference contains an invalid JSON Pointer escape.");
            }

            decoded.Append(token[index] == '0' ? '~' : '/');
        }

        return decoded.ToString();
    }

    private void EnsureRecursiveReferenceAdvancesInstance(
        string sourcePointer,
        string targetPointer,
        string reference)
    {
        int targetIndex = _stack.FindLastIndex(
            frame => string.Equals(frame.Pointer, targetPointer, StringComparison.Ordinal));
        bool advancesInstance = targetIndex >= 0
            && _stack
                .Skip(targetIndex + 1)
                .Any(static frame => frame.AdvancesInstance);
        if (!advancesInstance)
        {
            throw Failure(
                sourcePointer,
                "$ref",
                $"reference '{reference}' forms a non-progressing cycle; recursive references must pass through 'properties' or 'items'.");
        }
    }

    private void ValidateReferenceSiblings(JsonElement element, string pointer, bool isRoot)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool allowed = property.Name is "$ref" or "title" or "description" or "default" or "format"
                || (isRoot && property.Name is "$schema" or "$id" or "definitions");
            if (!allowed)
            {
                throw Failure(
                    pointer,
                    property.Name,
                    "assertion keywords beside '$ref' are ignored by Draft-07 and are therefore rejected.");
            }
        }
    }

    private void EnsureUniqueMembers(JsonElement element, string pointer, string keyword)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw Failure(pointer, keyword, $"object member '{property.Name}' is duplicated.");
            }
        }
    }

    private void ValidateOptionalString(JsonElement element, string pointer, string keyword)
    {
        if (element.TryGetProperty(keyword, out JsonElement value)
            && value.ValueKind != JsonValueKind.String)
        {
            throw Failure(pointer, keyword, "value must be a string.");
        }
    }

    private string ReadString(JsonElement element, string pointer, string keyword)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Failure(pointer, keyword, "value must be a string.");
        }

        return element.GetString()!;
    }

    private InvalidOperationException Failure(
        string pointer,
        string keyword,
        string message,
        Exception? innerException = null)
    {
        string location = pointer.Length == 0 ? "#" : $"#{pointer}";
        return new InvalidOperationException(
            $"Schema '{_sourceName}' at '{location}': keyword '{keyword}' {message}",
            innerException);
    }

    private static bool IsAllowedKeyword(string keyword)
    {
        return keyword is "$schema"
            or "$id"
            or "definitions"
            or "$ref"
            or "type"
            or "enum"
            or "const"
            or "required"
            or "properties"
            or "items"
            or "minLength"
            or "maxLength"
            or "pattern"
            or "minItems"
            or "maxItems"
            or "uniqueItems"
            or "minimum"
            or "maximum"
            or "oneOf"
            or "not"
            or "title"
            or "description"
            or "default"
            or "format";
    }

    private static string AppendPointer(string pointer, string token)
        => $"{pointer}/{token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    private sealed record CompilationFrame(string Pointer, bool AdvancesInstance);
}
