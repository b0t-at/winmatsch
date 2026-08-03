using System.Text.Json;

namespace WinMatsch.Validation.Schema;

internal sealed class JsonDeepEquality : IEqualityComparer<JsonElement>
{
    internal static JsonDeepEquality Instance { get; } = new();

    private JsonDeepEquality()
    {
    }

    public bool Equals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return JsonNumber.TryParse(left.GetRawText(), out JsonNumber leftNumber)
                && JsonNumber.TryParse(right.GetRawText(), out JsonNumber rightNumber)
                && leftNumber == rightNumber;
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right),
            JsonValueKind.Array => ArraysEqual(left, right),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            _ => false,
        };
    }

    public int GetHashCode(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => GetObjectHashCode(value),
            JsonValueKind.Array => GetArrayHashCode(value),
            JsonValueKind.Number => GetNumberHashCode(value),
            JsonValueKind.String => HashCode.Combine(
                JsonValueKind.String,
                StringComparer.Ordinal.GetHashCode(value.GetString() ?? string.Empty)),
            JsonValueKind.True => HashCode.Combine(JsonValueKind.True, true),
            JsonValueKind.False => HashCode.Combine(JsonValueKind.False, false),
            JsonValueKind.Null => JsonValueKind.Null.GetHashCode(),
            JsonValueKind.Undefined => JsonValueKind.Undefined.GetHashCode(),
            _ => value.ValueKind.GetHashCode(),
        };
    }

    private static bool ArraysEqual(JsonElement left, JsonElement right)
    {
        if (left.GetArrayLength() != right.GetArrayLength())
        {
            return false;
        }

        JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
        JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
        while (leftItems.MoveNext() && rightItems.MoveNext())
        {
            if (!Instance.Equals(leftItems.Current, rightItems.Current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        var rightProperties = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        int rightCount = 0;
        foreach (JsonProperty property in right.EnumerateObject())
        {
            if (!rightProperties.TryGetValue(property.Name, out List<JsonElement>? values))
            {
                values = [];
                rightProperties.Add(property.Name, values);
            }

            values.Add(property.Value);
            rightCount++;
        }

        int leftCount = 0;
        foreach (JsonProperty property in left.EnumerateObject())
        {
            leftCount++;
            if (!rightProperties.TryGetValue(property.Name, out List<JsonElement>? candidates))
            {
                return false;
            }

            int match = candidates.FindIndex(candidate => Instance.Equals(property.Value, candidate));
            if (match < 0)
            {
                return false;
            }

            candidates.RemoveAt(match);
        }

        return leftCount == rightCount;
    }

    private static int GetArrayHashCode(JsonElement value)
    {
        var hash = new HashCode();
        hash.Add(JsonValueKind.Array);
        foreach (JsonElement item in value.EnumerateArray())
        {
            hash.Add(Instance.GetHashCode(item));
        }

        return hash.ToHashCode();
    }

    private static int GetObjectHashCode(JsonElement value)
    {
        int sum = 0;
        int xor = 0;
        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            int pair = HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(property.Name),
                Instance.GetHashCode(property.Value));
            sum = unchecked(sum + pair);
            xor ^= pair;
            count++;
        }

        return HashCode.Combine(JsonValueKind.Object, count, sum, xor);
    }

    private static int GetNumberHashCode(JsonElement value)
    {
        return JsonNumber.TryParse(value.GetRawText(), out JsonNumber number)
            ? HashCode.Combine(JsonValueKind.Number, number)
            : HashCode.Combine(JsonValueKind.Number, value.GetRawText());
    }
}
