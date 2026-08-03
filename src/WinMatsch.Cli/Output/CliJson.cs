using System.Text.Json;

namespace WinMatsch.Cli.Output;

/// <summary>Stable JSON contract helpers shared by every command family.</summary>
public static class CliJson
{
    /// <summary>
    /// Version 1 preserves all 0.x properties and their wire values. Every enum also gains a
    /// camel-case <c>*Code</c> companion so new consumers have one consistent representation
    /// without breaking existing property readers.
    /// </summary>
    public const string SchemaVersion = "1.0";

    public static string EnumValue<T>(T value)
        where T : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static void WriteEnum<T>(
        Utf8JsonWriter writer,
        string name,
        T value,
        string? legacyValue = null)
        where T : struct, Enum
    {
        string canonical = EnumValue(value);
        writer.WriteString(name, legacyValue ?? canonical);
        writer.WriteString(name + "Code", canonical);
    }

    public static void WriteNullableEnum<T>(
        Utf8JsonWriter writer,
        string name,
        T? value,
        string? legacyValue = null)
        where T : struct, Enum
    {
        if (value is null)
        {
            writer.WriteNull(name);
            writer.WriteNull(name + "Code");
            return;
        }

        WriteEnum(writer, name, value.Value, legacyValue);
    }
}
