using System.Text.Json;
using WinMatsch.Validation.Schema;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class Draft7EvaluatorTests
{
    [Theory]
    [InlineData("\"integer\"", "1", true)]
    [InlineData("\"integer\"", "1.0", true)]
    [InlineData("\"integer\"", "1e9999", true)]
    [InlineData("\"integer\"", "1.5", false)]
    [InlineData("\"number\"", "1.5", true)]
    [InlineData("\"number\"", "\"1\"", false)]
    [InlineData("\"null\"", "null", true)]
    [InlineData("\"boolean\"", "false", true)]
    [InlineData("\"object\"", "{}", true)]
    [InlineData("\"array\"", "[]", true)]
    [InlineData("\"string\"", "\"value\"", true)]
    [InlineData("[\"string\",\"null\"]", "null", true)]
    [InlineData("[\"string\",\"null\"]", "\"value\"", true)]
    [InlineData("[\"string\",\"null\"]", "1", false)]
    public void Type_uses_draft7_number_and_union_semantics(
        string type,
        string instance,
        bool expected)
    {
        Draft7EvaluationResult result = Evaluate($"{{\"type\":{type}}}", instance);

        Assert.Equal(expected, result.IsValid);
    }

    [Theory]
    [InlineData("""{"minimum":-2147483648,"maximum":4294967295}""", "-2147483648", true)]
    [InlineData("""{"minimum":-2147483648,"maximum":4294967295}""", "4294967295.0", true)]
    [InlineData("""{"minimum":-2147483648,"maximum":4294967295}""", "-2147483649", false)]
    [InlineData("""{"minimum":-2147483648,"maximum":4294967295}""", "4294967296", false)]
    [InlineData("""{"minimum":1e9998,"maximum":1e10000}""", "1e9999", true)]
    [InlineData("""{"minimum":1e9998}""", "9e9997", false)]
    public void Numeric_bounds_compare_exact_values_without_overflow(
        string schema,
        string instance,
        bool expected)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.Equal(expected, result.IsValid);
    }

    [Theory]
    [InlineData("""{"enum":[0]}""", "0.0")]
    [InlineData("""{"enum":[0]}""", "-0")]
    [InlineData("""{"enum":[1.0]}""", "1")]
    [InlineData("""{"const":{"a":[1,2]}}""", """{"a":[1.0,2.0]}""")]
    [InlineData("""{"const":{"a":1,"b":2}}""", """{"b":2.0,"a":1.0}""")]
    public void Enum_and_const_use_deep_mathematical_json_equality(
        string schema,
        string instance)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Theory]
    [InlineData("""{"uniqueItems":true}""", """[0,0.0]""", false)]
    [InlineData("""{"uniqueItems":true}""", """[{"a":1,"b":2},{"b":2.0,"a":1.0}]""", false)]
    [InlineData("""{"uniqueItems":true}""", """[[1,2],[2,1]]""", true)]
    [InlineData("""{"uniqueItems":true}""", """[{"a":1},{"a":2}]""", true)]
    public void Unique_items_uses_deep_mathematical_json_equality(
        string schema,
        string instance,
        bool expected)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void String_lengths_count_unicode_code_points()
    {
        const string oneCodePoint = "\"\\uD83D\\uDE00\"";

        Draft7EvaluationResult exact = Evaluate("""{"minLength":1,"maxLength":1}""", oneCodePoint);
        Draft7EvaluationResult tooLong = Evaluate("""{"maxLength":0}""", oneCodePoint);

        Assert.True(exact.IsValid, FormatErrors(exact));
        Assert.Contains(tooLong.Errors, static error => error.Keyword == "maxLength");
    }

    [Theory]
    [InlineData("\"before123after\"", true)]
    [InlineData("\"123\"", true)]
    [InlineData("\"before-after\"", false)]
    public void Pattern_is_an_unanchored_ecmascript_search(string instance, bool expected)
    {
        Draft7EvaluationResult result = Evaluate("""{"pattern":"[0-9]+"}""", instance);

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void Required_emits_one_error_listing_all_missing_properties()
    {
        Draft7EvaluationResult result = Evaluate(
            """{"type":"object","required":["First","Second"]}""",
            "{}");

        SchemaEvaluationError error = Assert.Single(result.Errors);
        Assert.Equal("required", error.Keyword);
        Assert.Contains("\"First\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"Second\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_properties_and_items_report_rfc6901_instance_locations()
    {
        const string schema = """
            {
              "properties": {
                "a/b~c": {
                  "items": { "type": "string" }
                }
              }
            }
            """;

        Draft7EvaluationResult result = Evaluate(schema, """{"a/b~c":["ok",2]}""");

        SchemaEvaluationError error = Assert.Single(result.Errors);
        Assert.Equal("/a~1b~0c/1", error.InstanceLocation);
        Assert.Equal("type", error.Keyword);
    }

    [Theory]
    [InlineData("""{"minItems":2}""", "[1]", "minItems")]
    [InlineData("""{"maxItems":1}""", "[1,2]", "maxItems")]
    [InlineData("""{"minLength":2}""", "\"a\"", "minLength")]
    [InlineData("""{"maxLength":1}""", "\"ab\"", "maxLength")]
    public void Cardinality_keywords_enforce_inclusive_boundaries(
        string schema,
        string instance,
        string keyword)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.Contains(result.Errors, error => error.Keyword == keyword);
    }

    [Theory]
    [InlineData("""{"minLength":10,"minItems":10,"required":["Value"]}""", "42")]
    [InlineData("""{"properties":{"Value":{"type":"string"}}}""", "[]")]
    [InlineData("""{"items":{"type":"string"}}""", "{}")]
    public void Type_specific_keywords_ignore_other_instance_types(
        string schema,
        string instance)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Theory]
    [InlineData("""{"oneOf":[{"const":1},{"const":2}]}""", "3", false, 1)]
    [InlineData("""{"oneOf":[{"const":1},{"const":2}]}""", "1", true, 0)]
    [InlineData("""{"oneOf":[{"type":"number"},{"minimum":0}]}""", "1", false, 1)]
    public void One_of_requires_exactly_one_matching_subschema(
        string schema,
        string instance,
        bool expected,
        int oneOfErrors)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.Equal(expected, result.IsValid);
        Assert.Equal(oneOfErrors, result.Errors.Count(error => error.Keyword == "oneOf"));
    }

    [Theory]
    [InlineData("""{"not":{"enum":[0]}}""", "0.0", false)]
    [InlineData("""{"not":{"enum":[0]}}""", "1", true)]
    public void Not_discards_child_failures_and_fails_on_child_success(
        string schema,
        string instance,
        bool expected)
    {
        Draft7EvaluationResult result = Evaluate(schema, instance);

        Assert.Equal(expected, result.IsValid);
    }

    [Fact]
    public void Internal_reference_chains_validate_at_the_instance_location()
    {
        const string schema = """
            {
              "definitions": {
                "Text": { "type": "string", "minLength": 2 },
                "Alias": { "$ref": "#/definitions/Text" }
              },
              "properties": {
                "Value": { "$ref": "#/definitions/Alias" }
              }
            }
            """;

        Draft7EvaluationResult result = Evaluate(schema, """{"Value":"x"}""");

        SchemaEvaluationError error = Assert.Single(result.Errors);
        Assert.Equal("/Value", error.InstanceLocation);
        Assert.Equal("minLength", error.Keyword);
    }

    [Fact]
    public void Recursive_references_descend_through_finite_instance_trees()
    {
        const string schema = """
            {
              "$schema": "http://json-schema.org/draft-07/schema#",
              "definitions": {
                "Node": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "children": {
                      "type": "array",
                      "items": { "$ref": "#/definitions/Node" }
                    }
                  },
                  "required": [ "name" ]
                }
              },
              "$ref": "#/definitions/Node"
            }
            """;

        Draft7EvaluationResult valid = Evaluate(
            schema,
            """{"name":"root","children":[{"name":"leaf","children":[]}]}""");
        Draft7EvaluationResult invalid = Evaluate(
            schema,
            """{"name":"root","children":[{"children":[]}]}""");

        Assert.True(valid.IsValid, FormatErrors(valid));
        SchemaEvaluationError error = Assert.Single(invalid.Errors);
        Assert.Equal("/children/0", error.InstanceLocation);
        Assert.Equal("required", error.Keyword);
    }

    [Fact]
    public void Error_order_is_deterministic_and_follows_instance_traversal()
    {
        const string schema = """
            {
              "properties": {
                "First": { "type": "string", "minLength": 2 },
                "Second": { "type": "integer" }
              }
            }
            """;

        Draft7EvaluationResult first = Evaluate(schema, """{"Second":"bad","First":"x"}""");
        Draft7EvaluationResult second = Evaluate(schema, """{"Second":"bad","First":"x"}""");

        Assert.Equal(first.Errors, second.Errors);
        Assert.Collection(
            first.Errors,
            error => Assert.Equal("/First", error.InstanceLocation),
            error => Assert.Equal("/Second", error.InstanceLocation));
    }

    private static Draft7EvaluationResult Evaluate(string schemaJson, string instanceJson)
    {
        Draft7Schema schema = Draft7SchemaCompiler.Compile(schemaJson, "test-schema.json");
        using JsonDocument instance = JsonDocument.Parse(instanceJson);
        return Draft7Evaluator.Evaluate(schema, instance.RootElement);
    }

    private static string FormatErrors(Draft7EvaluationResult result)
        => string.Join(
            Environment.NewLine,
            result.Errors.Select(static error => $"{error.InstanceLocation} {error.Keyword}: {error.Message}"));
}
