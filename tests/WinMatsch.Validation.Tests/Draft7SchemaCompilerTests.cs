using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMatsch.Validation.Schema;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class Draft7SchemaCompilerTests
{
    private static readonly string[] _schemaResources =
    [
        "WinMatsch.Validation.Schemas.manifest.version.1.12.0.json",
        "WinMatsch.Validation.Schemas.manifest.installer.1.12.0.json",
        "WinMatsch.Validation.Schemas.manifest.defaultLocale.1.12.0.json",
        "WinMatsch.Validation.Schemas.manifest.locale.1.12.0.json",
    ];

    [Fact]
    public void All_bundled_schemas_and_patterns_compile()
    {
        Assembly assembly = typeof(ManifestSchemaValidator).Assembly;
        int patternCount = 0;

        foreach (string resourceName in _schemaResources)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            using JsonDocument document = JsonDocument.Parse(json);
            patternCount += CountKeyword(document.RootElement, "pattern");

            Draft7Schema schema = Draft7SchemaCompiler.Compile(json, resourceName);

            Assert.NotNull(schema);
        }

        Assert.Equal(31, patternCount);
    }

    [Fact]
    public void Pattern_uses_ecmascript_mode_and_a_finite_timeout()
    {
        Draft7Schema schema = Draft7SchemaCompiler.Compile("""{"pattern":"[0-9]+"}""");

        Regex pattern = Assert.IsType<Regex>(schema.Pattern);
        Assert.True(pattern.Options.HasFlag(RegexOptions.ECMAScript));
        Assert.True(pattern.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.Equal(Draft7SchemaCompiler.PatternMatchTimeout, pattern.MatchTimeout);
    }

    [Theory]
    [InlineData("""{"futureKeyword":true}""", "#", "futureKeyword")]
    [InlineData("""{"definitions":{"Value":{"futureKeyword":true}}}""", "#/definitions/Value", "futureKeyword")]
    [InlineData("""{"properties":{"Value":{"futureKeyword":true}}}""", "#/properties/Value", "futureKeyword")]
    [InlineData("""{"items":{"futureKeyword":true}}""", "#/items", "futureKeyword")]
    [InlineData("""{"oneOf":[{"futureKeyword":true}]}""", "#/oneOf/0", "futureKeyword")]
    [InlineData("""{"not":{"futureKeyword":true}}""", "#/not", "futureKeyword")]
    public void Unknown_keywords_fail_at_their_schema_location(
        string json,
        string expectedLocation,
        string keyword)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json, "gate-test.json"));

        Assert.Contains("gate-test.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedLocation, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"keyword '{keyword}'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"items":[{"type":"string"}]}""", "items")]
    [InlineData("""{"properties":{"Value":{"$id":"child"}}}""", "$id")]
    [InlineData("""{"type":"wat"}""", "type")]
    [InlineData("""{"type":[]}""", "type")]
    [InlineData("""{"type":["string","string"]}""", "type")]
    [InlineData("""{"enum":[]}""", "enum")]
    [InlineData("""{"enum":[0,0.0]}""", "enum")]
    [InlineData("""{"required":["Value","Value"]}""", "required")]
    [InlineData("""{"required":"Value"}""", "required")]
    [InlineData("""{"minLength":-1}""", "minLength")]
    [InlineData("""{"maxItems":1.5}""", "maxItems")]
    [InlineData("""{"uniqueItems":"true"}""", "uniqueItems")]
    [InlineData("""{"minimum":"0"}""", "minimum")]
    [InlineData("""{"oneOf":[]}""", "oneOf")]
    [InlineData("""{"pattern":"["}""", "pattern")]
    [InlineData("""{"description":1}""", "description")]
    [InlineData("""{"format":false}""", "format")]
    [InlineData("""{"$schema":"https://json-schema.org/draft/2020-12/schema"}""", "$schema")]
    public void Malformed_or_unsupported_keyword_values_fail_loudly(string json, string keyword)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json));

        Assert.Contains($"keyword '{keyword}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boolean_schemas_are_rejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile("true"));

        Assert.Contains("non-object schemas", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_schema_keywords_are_rejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile("""{"type":"string","type":"number"}"""));

        Assert.Contains("duplicate schema keywords", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_references_are_rejected()
    {
        const string json = """
            {
              "definitions": {
                "Known": { "type": "string" }
              },
              "properties": {
                "Value": { "$ref": "#/definitions/Missing" }
              }
            }
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json));

        Assert.Contains("does not resolve", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cyclic_references_are_rejected()
    {
        const string json = """
            {
              "definitions": {
                "First": { "$ref": "#/definitions/Second" },
                "Second": { "$ref": "#/definitions/First" }
              }
            }
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json));

        Assert.Contains("forms a non-progressing cycle", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"definitions":{"Loop":{"not":{"$ref":"#/definitions/Loop"}}}}""")]
    [InlineData("""{"definitions":{"Loop":{"oneOf":[{"$ref":"#/definitions/Loop"}]}}}""")]
    public void Same_instance_recursive_combinators_are_rejected(string json)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json));

        Assert.Contains("forms a non-progressing cycle", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'properties' or 'items'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Instance_descending_recursive_references_are_accepted()
    {
        const string json = """
            {
              "$schema": "http://json-schema.org/draft-07/schema#",
              "definitions": {
                "Node": {
                  "type": "object",
                  "properties": {
                    "children": {
                      "type": "array",
                      "items": { "$ref": "#/definitions/Node" }
                    }
                  }
                }
              },
              "$ref": "#/definitions/Node"
            }
            """;

        Draft7Schema schema = Draft7SchemaCompiler.Compile(json);

        Assert.NotNull(schema.Reference);
    }

    [Fact]
    public void Assertion_siblings_beside_ref_are_rejected()
    {
        const string json = """
            {
              "definitions": {
                "Value": { "type": "string" }
              },
              "properties": {
                "Value": {
                  "$ref": "#/definitions/Value",
                  "minLength": 1
                }
              }
            }
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile(json));

        Assert.Contains("beside '$ref'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ref_annotations_and_default_annotation_are_accepted()
    {
        const string json = """
            {
              "definitions": {
                "Value": { "type": "string", "default": "fallback" }
              },
              "properties": {
                "Value": {
                  "$ref": "#/definitions/Value",
                  "description": "A value",
                  "default": "fallback"
                }
              }
            }
            """;

        Draft7Schema schema = Draft7SchemaCompiler.Compile(json);

        Assert.NotNull(schema);
    }

    [Fact]
    public void Empty_required_array_is_a_valid_no_op()
    {
        Draft7Schema schema = Draft7SchemaCompiler.Compile("""{"required":[]}""");

        Assert.Empty(schema.RequiredProperties);
    }

    [Fact]
    public void References_outside_definitions_are_rejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => Draft7SchemaCompiler.Compile("""{"$ref":"#/properties/Value"}"""));

        Assert.Contains("#/definitions/", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tilde_and_slash_reference_pointer_escapes_resolve()
    {
        const string json = """
            {
              "definitions": {
                "tilde~field": { "type": "integer" },
                "slash/field": { "type": "string" }
              },
              "properties": {
                "tilde": { "$ref": "#/definitions/tilde~0field" },
                "slash": { "$ref": "#/definitions/slash~1field" }
              }
            }
            """;

        Draft7Schema schema = Draft7SchemaCompiler.Compile(json);
        using JsonDocument instance = JsonDocument.Parse("""{"tilde":1,"slash":"value"}""");

        Assert.True(Draft7Evaluator.Evaluate(schema, instance.RootElement).IsValid);
    }

    private static int CountKeyword(JsonElement element, string keyword)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Sum(item => CountKeyword(item, keyword));
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(keyword))
            {
                count++;
            }

            count += CountKeyword(property.Value, keyword);
        }

        return count;
    }
}
