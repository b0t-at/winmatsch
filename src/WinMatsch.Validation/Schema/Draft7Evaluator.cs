using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinMatsch.Validation.Schema;

internal static class Draft7Evaluator
{
    internal static Draft7EvaluationResult Evaluate(Draft7Schema schema, JsonElement instance)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<SchemaEvaluationError>();
        Evaluate(schema, instance, string.Empty, errors);
        return new Draft7EvaluationResult(errors);
    }

    private static void Evaluate(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.Reference is not null)
        {
            Evaluate(schema.Reference, instance, instanceLocation, errors);
            return;
        }

        EvaluateType(schema, instance, instanceLocation, errors);
        EvaluateEnum(schema, instance, instanceLocation, errors);
        EvaluateConstant(schema, instance, instanceLocation, errors);
        EvaluateNumber(schema, instance, instanceLocation, errors);
        EvaluateString(schema, instance, instanceLocation, errors);
        EvaluateArray(schema, instance, instanceLocation, errors);
        EvaluateObject(schema, instance, instanceLocation, errors);
        EvaluateOneOf(schema, instance, instanceLocation, errors);
        EvaluateNot(schema, instance, instanceLocation, errors);
    }

    private static void EvaluateType(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.Types == JsonSchemaType.None || IsAllowedType(schema.Types, instance))
        {
            return;
        }

        string allowed = schema.TypeNames.Count == 1
            ? $"\"{schema.TypeNames[0]}\""
            : $"[{string.Join(", ", schema.TypeNames.Select(static name => $"\"{name}\""))}]";
        errors.Add(new SchemaEvaluationError(
            instanceLocation,
            "type",
            $"Value is not of type {allowed}."));
    }

    private static bool IsAllowedType(JsonSchemaType types, JsonElement instance)
    {
        return instance.ValueKind switch
        {
            JsonValueKind.Null => (types & JsonSchemaType.Null) != 0,
            JsonValueKind.True or JsonValueKind.False => (types & JsonSchemaType.Boolean) != 0,
            JsonValueKind.Object => (types & JsonSchemaType.Object) != 0,
            JsonValueKind.Array => (types & JsonSchemaType.Array) != 0,
            JsonValueKind.String => (types & JsonSchemaType.String) != 0,
            JsonValueKind.Number => (types & JsonSchemaType.Number) != 0
                || ((types & JsonSchemaType.Integer) != 0
                    && JsonNumber.TryParse(instance.GetRawText(), out JsonNumber number)
                    && number.IsInteger),
            _ => false,
        };
    }

    private static void EvaluateEnum(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.EnumValues.Count != 0
            && !schema.EnumValues.Any(value => JsonDeepEquality.Instance.Equals(value, instance)))
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "enum",
                "Value is not one of the values specified by 'enum'."));
        }
    }

    private static void EvaluateConstant(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.Constant is JsonElement constant
            && !JsonDeepEquality.Instance.Equals(constant, instance))
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "const",
                "Value does not equal the value specified by 'const'."));
        }
    }

    private static void EvaluateNumber(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (instance.ValueKind != JsonValueKind.Number
            || !JsonNumber.TryParse(instance.GetRawText(), out JsonNumber number))
        {
            return;
        }

        if (schema.Minimum is JsonNumber minimum && number.CompareTo(minimum) < 0)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "minimum",
                $"Value is less than the inclusive minimum {FormatNumber(minimum)}."));
        }

        if (schema.Maximum is JsonNumber maximum && number.CompareTo(maximum) > 0)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "maximum",
                $"Value is greater than the inclusive maximum {FormatNumber(maximum)}."));
        }
    }

    private static void EvaluateString(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string value = instance.GetString()!;
        int length = value.EnumerateRunes().Count();
        if (schema.MinimumLength is int minimumLength && length < minimumLength)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "minLength",
                $"String length {length.ToString(CultureInfo.InvariantCulture)} is less than {minimumLength.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (schema.MaximumLength is int maximumLength && length > maximumLength)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "maxLength",
                $"String length {length.ToString(CultureInfo.InvariantCulture)} exceeds {maximumLength.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (schema.Pattern is not null)
        {
            try
            {
                if (!schema.Pattern.IsMatch(value))
                {
                    errors.Add(new SchemaEvaluationError(
                        instanceLocation,
                        "pattern",
                        $"String does not match pattern '{schema.Pattern}'."));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add(new SchemaEvaluationError(
                    instanceLocation,
                    "pattern",
                    $"Pattern evaluation exceeded the {Draft7SchemaCompiler.PatternMatchTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms timeout."));
            }
        }
    }

    private static void EvaluateArray(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int length = instance.GetArrayLength();
        if (schema.MinimumItems is int minimumItems && length < minimumItems)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "minItems",
                $"Array length {length.ToString(CultureInfo.InvariantCulture)} is less than {minimumItems.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (schema.MaximumItems is int maximumItems && length > maximumItems)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "maxItems",
                $"Array length {length.ToString(CultureInfo.InvariantCulture)} exceeds {maximumItems.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (schema.UniqueItems)
        {
            var unique = new HashSet<JsonElement>(JsonDeepEquality.Instance);
            foreach (JsonElement item in instance.EnumerateArray())
            {
                if (!unique.Add(item))
                {
                    errors.Add(new SchemaEvaluationError(
                        instanceLocation,
                        "uniqueItems",
                        "Array items are not unique."));
                    break;
                }
            }
        }

        if (schema.Items is not null)
        {
            int index = 0;
            foreach (JsonElement item in instance.EnumerateArray())
            {
                Evaluate(
                    schema.Items,
                    item,
                    AppendPointer(instanceLocation, index.ToString(CultureInfo.InvariantCulture)),
                    errors);
                index++;
            }
        }
    }

    private static void EvaluateObject(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        List<string>? missing = null;
        foreach (string required in schema.RequiredProperties)
        {
            if (!instance.TryGetProperty(required, out _))
            {
                (missing ??= []).Add(required);
            }
        }

        if (missing is not null)
        {
            string names = string.Join(
                ", ",
                missing.Select(static name => $"\"{JavaScriptEncoder.Default.Encode(name)}\""));
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "required",
                $"Required properties [{names}] are absent."));
        }

        foreach ((string name, Draft7Schema propertySchema) in schema.Properties)
        {
            if (instance.TryGetProperty(name, out JsonElement value))
            {
                Evaluate(propertySchema, value, AppendPointer(instanceLocation, name), errors);
            }
        }
    }

    private static void EvaluateOneOf(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.OneOf.Count == 0)
        {
            return;
        }

        int validCount = 0;
        var branchErrors = new List<SchemaEvaluationError>();
        foreach (Draft7Schema branch in schema.OneOf)
        {
            var currentErrors = new List<SchemaEvaluationError>();
            Evaluate(branch, instance, instanceLocation, currentErrors);
            if (currentErrors.Count == 0)
            {
                validCount++;
            }
            else
            {
                branchErrors.AddRange(currentErrors);
            }
        }

        if (validCount == 1)
        {
            return;
        }

        if (validCount == 0)
        {
            errors.AddRange(branchErrors);
        }

        errors.Add(new SchemaEvaluationError(
            instanceLocation,
            "oneOf",
            $"Value validated against {validCount.ToString(CultureInfo.InvariantCulture)} subschemas; exactly one is required."));
    }

    private static void EvaluateNot(
        Draft7Schema schema,
        JsonElement instance,
        string instanceLocation,
        List<SchemaEvaluationError> errors)
    {
        if (schema.Not is null)
        {
            return;
        }

        var childErrors = new List<SchemaEvaluationError>();
        Evaluate(schema.Not, instance, instanceLocation, childErrors);
        if (childErrors.Count == 0)
        {
            errors.Add(new SchemaEvaluationError(
                instanceLocation,
                "not",
                "Value validates against the prohibited schema."));
        }
    }

    private static string FormatNumber(JsonNumber number)
    {
        string sign = number.IsNegative ? "-" : string.Empty;
        return number.Exponent.Sign == 0
            ? $"{sign}{number.Digits}"
            : $"{sign}{number.Digits}e{number.Exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string AppendPointer(string pointer, string token)
        => $"{pointer}/{token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
}
