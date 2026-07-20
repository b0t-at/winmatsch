using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinMatsch.Core.Yaml;

/// <summary>
/// A purpose-built YAML emitter producing the exact output conventions used across winget-pkgs:
/// two-space indentation, block sequences at the parent key's indentation, plain scalars unless
/// quoting is required for correctness (then double quotes), literal block scalars for multi-line
/// strings, LF line endings and a single trailing newline.
/// Quoting is deliberately precise so that values a YAML 1.1/1.2 parser could misinterpret
/// (numbers, booleans, timestamps, sexagesimals) are quoted while ordinary strings stay plain.
/// </summary>
internal sealed partial class YamlEmitter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Comment(string text)
    {
        AppendIndent();
        _builder.Append("# ").Append(text).Append('\n');
    }

    public void BlankLine() => _builder.Append('\n');

    public void Scalar(string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        bool hasNewline = value.Contains('\n', StringComparison.Ordinal);
        bool hasOtherControlCharacters = ContainsNonNewlineControlCharacters(value);

        if (hasNewline && !hasOtherControlCharacters && CanUseBlockLiteral(value))
        {
            WriteBlockLiteral(key, value);
        }
        else if (hasNewline || hasOtherControlCharacters)
        {
            WriteKeyValue(key, DoubleQuote(value));
        }
        else
        {
            WriteKeyValue(key, NeedsQuoting(value) ? DoubleQuote(value) : value);
        }
    }

    public void Scalar(string key, bool? value)
    {
        if (value is not null)
        {
            WriteKeyValue(key, value.Value ? "true" : "false");
        }
    }

    public void Scalar(string key, long? value)
    {
        if (value is not null)
        {
            WriteKeyValue(key, value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    public void Scalar(string key, DateOnly? value)
    {
        if (value is not null)
        {
            WriteKeyValue(key, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Writes a sequence of string scalars; omitted entirely when null or empty.</summary>
    public void StringSequence(string key, IEnumerable<string>? items)
        => ScalarSequence(key, items, static item => item);

    /// <summary>Writes a sequence of scalars using a formatter; omitted entirely when null or empty.</summary>
    public void ScalarSequence<T>(string key, IEnumerable<T>? items, Func<T, string> format)
    {
        if (items is null)
        {
            return;
        }

        bool first = true;
        foreach (T item in items)
        {
            if (first)
            {
                WriteKey(key);
                first = false;
            }

            string formatted = format(item);
            AppendIndent();
            _builder.Append("- ").Append(NeedsQuoting(formatted) ? DoubleQuote(formatted) : formatted).Append('\n');
        }
    }

    /// <summary>Writes a sequence of integers as plain (unquoted) YAML numbers; omitted entirely when null or empty.</summary>
    public void NumberSequence(string key, IEnumerable<long>? items)
    {
        if (items is null)
        {
            return;
        }

        bool first = true;
        foreach (long item in items)
        {
            if (first)
            {
                WriteKey(key);
                first = false;
            }

            AppendIndent();
            _builder.Append("- ").Append(item.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>Writes a nested mapping. The caller is responsible for skipping empty mappings.</summary>
    public void Mapping(string key, Action<YamlEmitter> body)
    {
        WriteKey(key);
        _indent++;
        body(this);
        _indent--;
    }

    /// <summary>Writes a sequence of mappings; omitted entirely when null or empty.</summary>
    public void MappingSequence<T>(string key, IReadOnlyList<T>? items, Action<YamlEmitter, T> writeItem)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        WriteKey(key);
        foreach (T item in items)
        {
            var itemEmitter = new YamlEmitter();
            writeItem(itemEmitter, item);

            string rendered = itemEmitter._builder.ToString();
            bool firstLine = true;
            foreach (Range lineRange in rendered.AsSpan().TrimEnd('\n').Split('\n'))
            {
                ReadOnlySpan<char> line = rendered.AsSpan()[lineRange];
                AppendIndent();
                _builder.Append(firstLine ? "- " : "  ").Append(line).Append('\n');
                firstLine = false;
            }
        }
    }

    public override string ToString() => _builder.ToString();

    private void WriteKey(string key)
    {
        AppendIndent();
        _builder.Append(key).Append(":\n");
    }

    private void WriteKeyValue(string key, string renderedValue)
    {
        AppendIndent();
        _builder.Append(key).Append(": ").Append(renderedValue).Append('\n');
    }

    private void WriteBlockLiteral(string key, string value)
    {
        int trailingNewlines = 0;
        while (trailingNewlines < value.Length && value[^(trailingNewlines + 1)] == '\n')
        {
            trailingNewlines++;
        }

        string indicator = trailingNewlines switch
        {
            0 => "|-",
            1 => "|",
            _ => "|+",
        };

        // For clip chomping ("|"), the single trailing newline is implied by the block ending.
        string content = trailingNewlines == 1 ? value[..^1] : value;

        AppendIndent();
        _builder.Append(key).Append(": ").Append(indicator).Append('\n');

        _indent++;
        ReadOnlySpan<char> span = content.AsSpan();
        foreach (Range lineRange in span.Split('\n'))
        {
            ReadOnlySpan<char> line = span[lineRange];
            if (line.IsEmpty)
            {
                _builder.Append('\n');
            }
            else
            {
                AppendIndent();
                _builder.Append(line).Append('\n');
            }
        }

        _indent--;
    }

    private void AppendIndent() => _builder.Append(' ', _indent * 2);

    /// <summary>Whether a multi-line value can be represented as a literal block scalar without changing its content.</summary>
    private static bool CanUseBlockLiteral(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        foreach (Range lineRange in span.Split('\n'))
        {
            ReadOnlySpan<char> line = span[lineRange];

            // A line starting with whitespace would require an explicit indentation indicator
            // and lines with trailing spaces do not round-trip; fall back to double quoting.
            if (!line.IsEmpty && (char.IsWhiteSpace(line[0]) || char.IsWhiteSpace(line[^1])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsNonNewlineControlCharacters(string value)
    {
        foreach (char c in value)
        {
            if (c != '\n' && (char.IsControl(c) || c is '\u2028' or '\u2029'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a single-line value must be quoted to survive as a string through any YAML 1.1/1.2 parser.</summary>
    internal static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        char first = value[0];
        if (first is '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`' or ',' or '=')
        {
            return true;
        }

        // '-', '?' and ':' only act as indicators when followed by whitespace (or at end of scalar).
        if (first is '-' or '?' or ':' && (value.Length == 1 || value[1] is ' ' or '\t'))
        {
            return true;
        }

        if (value.Contains(": ", StringComparison.Ordinal) || value[^1] == ':')
        {
            return true;
        }

        if (value.Contains(" #", StringComparison.Ordinal) || value.Contains('\t', StringComparison.Ordinal))
        {
            return true;
        }

        // Values that YAML 1.1/1.2 resolvers would turn into non-string types.
        string lower = value.ToLowerInvariant();
        if (lower is "true" or "false" or "yes" or "no" or "on" or "off" or "y" or "n" or "null" or "~"
            || NumberLikeRegex().IsMatch(value)
            || TimestampLikeRegex().IsMatch(value))
        {
            return true;
        }

        return false;
    }

    private static string DoubleQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029')
                    {
                        builder.Append(c <= 0xFF
                            ? string.Create(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}")
                            : string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    // Integers (decimal with YAML 1.1 underscores, hex, octal both 1.1 and 1.2 style, binary,
    // sexagesimal), floats (including leading-dot, exponent and .inf/.nan forms).
    [GeneratedRegex(
        """^[-+]?(\d[\d_]*|0x[\dA-Fa-f_]+|0o?[0-7_]+|0b[01_]+|\d[\d_]*(:[0-5]?\d)+(\.[\d_]*)?|(\.\d[\d_]*|\d[\d_]*(\.[\d_]*)?)([eE][-+]?\d+)?|\.(inf|Inf|INF)|\.(nan|NaN|NAN))$""")]
    private static partial Regex NumberLikeRegex();

    // YAML 1.1 timestamps: 2001-12-15, 2001-12-15T02:59:43.1Z, 2001-12-14 21:59:43.10 -5, ...
    [GeneratedRegex("""^\d{4}-\d{1,2}-\d{1,2}([Tt ].*)?$""")]
    private static partial Regex TimestampLikeRegex();
}
