using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinMatsch.Validation.Schema;

[Flags]
internal enum JsonSchemaType
{
    None = 0,
    Null = 1 << 0,
    Boolean = 1 << 1,
    Object = 1 << 2,
    Array = 1 << 3,
    Number = 1 << 4,
    String = 1 << 5,
    Integer = 1 << 6,
}

internal sealed class Draft7Schema
{
    internal Draft7Schema? Reference { get; set; }

    internal JsonSchemaType Types { get; set; }

    internal IReadOnlyList<string> TypeNames { get; set; } = [];

    internal IReadOnlyList<JsonElement> EnumValues { get; set; } = [];

    internal JsonElement? Constant { get; set; }

    internal IReadOnlyList<string> RequiredProperties { get; set; } = [];

    internal IReadOnlyDictionary<string, Draft7Schema> Properties { get; set; }
        = new Dictionary<string, Draft7Schema>(StringComparer.Ordinal);

    internal Draft7Schema? Items { get; set; }

    internal int? MinimumLength { get; set; }

    internal int? MaximumLength { get; set; }

    internal Regex? Pattern { get; set; }

    internal int? MinimumItems { get; set; }

    internal int? MaximumItems { get; set; }

    internal bool UniqueItems { get; set; }

    internal JsonNumber? Minimum { get; set; }

    internal JsonNumber? Maximum { get; set; }

    internal IReadOnlyList<Draft7Schema> OneOf { get; set; } = [];

    internal Draft7Schema? Not { get; set; }
}
