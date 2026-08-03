using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinMatsch.Validation;

/// <summary>The ordered findings produced by a complete validation run.</summary>
public sealed class ValidationReport
{
    private readonly IReadOnlyList<ValidationFinding> _findings;

    public ValidationReport(IEnumerable<ValidationFinding>? findings = null)
    {
        _findings = findings is null ? [] : [.. findings];
    }

    public IReadOnlyList<ValidationFinding> Findings => _findings;

    public bool IsValid => !HasErrors;

    public bool HasErrors
    {
        get
        {
            foreach (ValidationFinding finding in _findings)
            {
                if (finding.Severity == ValidationSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool HasWarnings
    {
        get
        {
            foreach (ValidationFinding finding in _findings)
            {
                if (finding.Severity == ValidationSeverity.Warning)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether this report permits crossing a commit or pull-request boundary.</summary>
    public bool CanProceed(WarningPolicy warningPolicy = WarningPolicy.Allow)
        => !HasErrors && (warningPolicy != WarningPolicy.TreatAsErrors || !HasWarnings);

    /// <summary>Formats deterministic, line-oriented diagnostics for terminals and logs.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (ValidationFinding finding in _findings)
        {
            builder.Append(finding.Severity.ToString().ToLowerInvariant());
            builder.Append(' ');
            builder.Append(finding.Code);
            if (finding.Path is not null)
            {
                builder.Append(" [");
                builder.Append(finding.Path);
                builder.Append(']');
            }

            builder.Append(": ");
            builder.Append(finding.Message);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Formats deterministic JSON without reflection-based serialization.</summary>
    public string ToJson(bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = indented,
            }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("isValid", IsValid);
            writer.WriteStartArray("findings");
            foreach (ValidationFinding finding in _findings)
            {
                writer.WriteStartObject();
                writer.WriteString("code", finding.Code);
                writer.WriteString("severity", finding.Severity.ToString().ToLowerInvariant());
                writer.WriteString("message", finding.Message);
                if (finding.Path is not null)
                {
                    writer.WriteString("path", finding.Path);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
