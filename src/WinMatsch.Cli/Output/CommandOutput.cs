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
        _output.WriteLine(text);
    }

    public void WriteJsonResult(Action<Utf8JsonWriter> writeDocument)
    {
        ArgumentNullException.ThrowIfNull(writeDocument);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writeDocument(writer);
        }

        _output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
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
            writeText(_output);
        }
    }

    public void WriteDiagnostic(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _error.WriteLine(message);
    }

    public void WriteError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _error.WriteLine(message);
    }
}
