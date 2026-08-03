using System.Text;
using System.Text.Json;
using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Output;

/// <summary>
/// The standard <see cref="ICommandOutput"/> over the host's standard output and standard
/// error writers. See <see cref="ICommandOutput"/> for the stream and stability contract.
/// </summary>
public sealed class CommandOutput : ICommandOutput
{
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Indented = false,
        // Results are data, not HTML; keep non-ASCII characters readable and byte-stable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public CommandOutput(TextWriter output, TextWriter error, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _output = output;
        _error = error;
        Format = format;
    }

    public OutputFormat Format { get; }

    public void WriteResult(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _output.WriteLine(CliRedactor.Redact(text));
    }

    public void WriteJsonResult(Action<Utf8JsonWriter> writeDocument)
    {
        ArgumentNullException.ThrowIfNull(writeDocument);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writeDocument(writer);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("CLI JSON results must be root objects.");
        }

        using var envelope = new MemoryStream();
        using (var writer = new Utf8JsonWriter(envelope, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", CliJson.SchemaVersion);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("schemaVersion"))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                if (CliRedactor.IsSecretKey(property.Name))
                {
                    writer.WriteStringValue(CliRedactor.Placeholder);
                }
                else
                {
                    WriteRedactedJson(writer, property.Value);
                }
            }

            writer.WriteEndObject();
        }

        _output.WriteLine(Encoding.UTF8.GetString(envelope.ToArray()));
    }

    public void WriteFormatted(Action<TextWriter> writeText, Action<Utf8JsonWriter> writeJson)
    {
        ArgumentNullException.ThrowIfNull(writeText);
        ArgumentNullException.ThrowIfNull(writeJson);
        if (Format == OutputFormat.Json)
        {
            WriteJsonResult(writeJson);
        }
        else
        {
            using var buffer = new StringWriter();
            buffer.NewLine = _output.NewLine;
            writeText(buffer);
            _output.Write(CliRedactor.Redact(buffer.ToString()));
        }
    }

    public void WriteDiagnostic(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _error.WriteLine(CliRedactor.Redact(message));
    }

    public void WriteError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _error.WriteLine(CliRedactor.Redact(message));
    }

    private static void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (CliRedactor.IsSecretKey(property.Name))
                    {
                        writer.WriteStringValue(CliRedactor.Placeholder);
                    }
                    else
                    {
                        WriteRedactedJson(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedactedJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(CliRedactor.Redact(element.GetString() ?? ""));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
