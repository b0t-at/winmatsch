using System.Text.Json;

namespace WinMatsch.Cli.Output;

/// <summary>
/// Where command results and diagnostics go, and in which format.
///
/// <para>Stream contract (stable; scripts may rely on it):</para>
/// <list type="bullet">
/// <item><em>Standard output</em> carries results only: human-readable text in text mode, one
/// stable JSON document in JSON mode. Nothing else is ever written there.</item>
/// <item><em>Standard error</em> carries diagnostics, warnings, and error messages as plain
/// text in both formats.</item>
/// <item>JSON output is deterministic: fixed property order chosen by the writer, invariant
/// formatting, no indentation, terminated by a single newline.</item>
/// </list>
/// </summary>
public interface ICommandOutput
{
    /// <summary>The negotiated result format for this invocation.</summary>
    public WinMatsch.Workflows.Configuration.OutputFormat Format { get; }

    /// <summary>Writes a human-readable result line to standard output (text mode).</summary>
    public void WriteResult(string text);

    /// <summary>
    /// Writes one stable JSON document to standard output (JSON mode). The callback writes the
    /// document body; the host owns encoding, determinism, and the trailing newline.
    /// </summary>
    public void WriteJsonResult(Action<Utf8JsonWriter> writeDocument);

    /// <summary>
    /// Writes the result in the negotiated format: exactly one of the callbacks runs.
    /// </summary>
    public void WriteFormatted(Action<TextWriter> writeText, Action<Utf8JsonWriter> writeJson);

    /// <summary>Writes a diagnostic message to standard error.</summary>
    public void WriteDiagnostic(string message);

    /// <summary>Writes an error message to standard error.</summary>
    public void WriteError(string message);
}
