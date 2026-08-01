using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinMatsch.Cli.Output;

/// <summary>
/// The bounded, fail-closed redaction contract for every CLI boundary. Regex work is performed
/// in overlapping fixed-size windows, preserving unaffected output while bounding each match.
/// </summary>
public static partial class CliRedactor
{
    public const string Placeholder = "[REDACTED]";

    private const int RegexTimeoutMilliseconds = 100;
    private const int ChunkLength = 32 * 1024;
    private const int OverlapLength = 16 * 1024;

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = ReplaceBounded(
            value,
            GitHubTokenPattern(),
            static _ => Placeholder);
        redacted = ReplaceBounded(
            redacted,
            JwtPattern(),
            static match => IsJwt(match.Value) ? Placeholder : match.Value);
        redacted = RedactSecretAssignments(redacted);
        redacted = ReplaceBounded(
            redacted,
            AuthorizationPattern(),
            static match => match.Groups[1].Value + Placeholder);
        redacted = ReplaceBounded(
            redacted,
            UriUserInfoPattern(),
            static match => match.Groups[1].Value + Placeholder + "@");
        return RedactQueryValues(redacted, redactAll: false);
    }

    /// <summary>
    /// Redacts a displayed URL through the same bounded engine. Cache output sets
    /// <paramref name="redactAllQueryValues"/> because arbitrary download query keys can carry
    /// credentials.
    /// </summary>
    public static string RedactUrl(string value, bool redactAllQueryValues)
    {
        string redacted = Redact(value);
        return redactAllQueryValues ? RedactQueryValues(redacted, redactAll: true) : redacted;
    }

    public static string? RedactNullable(string? value) => value is null ? null : Redact(value);

    public static bool IsSecretKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = NormalizeKey(value);
        return normalized is "password"
            or "clientsecret"
            or "accesstoken"
            or "refreshtoken"
            or "token"
            or "githubtoken"
            or "secret"
            or "apikey"
            or "signature"
            or "sig"
            or "credential"
            or "authorization"
            or "auth";
    }

    private static string RedactQueryValues(string value, bool redactAll)
        => ReplaceBounded(
            value,
            QueryPairPattern(),
            match =>
            {
                string encodedKey = match.Groups["key"].Value;
                string key;
                try
                {
                    key = Uri.UnescapeDataString(encodedKey);
                }
                catch (UriFormatException)
                {
                    key = encodedKey;
                }

                return redactAll || IsSensitiveQueryKey(key)
                    ? match.Groups[1].Value + encodedKey + "=" + Placeholder
                    : match.Value;
            });

    private static string RedactSecretAssignments(string input)
    {
        var replacements = new List<Replacement>();
        for (int index = 0; index < input.Length;)
        {
            int keyStart = index;
            char quote = '\0';
            bool escapedQuote = false;
            if (input[index] == '\\'
                && index + 1 < input.Length
                && input[index + 1] is '"' or '\'')
            {
                escapedQuote = true;
                quote = input[index + 1];
                index += 2;
            }
            else if (input[index] is '"' or '\'')
            {
                quote = input[index++];
            }

            int nameStart = index;
            while (index < input.Length
                   && (char.IsAsciiLetterOrDigit(input[index])
                       || input[index] is '_' or '-' or '.')
                   && index - nameStart <= 32)
            {
                index++;
            }

            if (nameStart == index)
            {
                index = keyStart + 1;
                continue;
            }

            if (index < input.Length
                && (char.IsAsciiLetterOrDigit(input[index])
                    || input[index] is '_' or '-' or '.'))
            {
                while (index < input.Length
                       && (char.IsAsciiLetterOrDigit(input[index])
                           || input[index] is '_' or '-' or '.'))
                {
                    index++;
                }

                continue;
            }

            string key = input[nameStart..index];
            if (!IsSecretKey(key))
            {
                continue;
            }

            if (quote != '\0')
            {
                if (escapedQuote
                    && index + 1 < input.Length
                    && input[index] == '\\'
                    && input[index + 1] == quote)
                {
                    index += 2;
                }
                else if (!escapedQuote && index < input.Length && input[index] == quote)
                {
                    index++;
                }
                else
                {
                    index = keyStart + 1;
                    continue;
                }
            }

            while (index < input.Length && char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            if (index >= input.Length || input[index] is not (':' or '='))
            {
                index = keyStart + 1;
                continue;
            }

            index++;
            while (index < input.Length && char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            if (index >= input.Length)
            {
                break;
            }

            int valueStart = index;
            int valueEnd;
            if (input[index] == '\\'
                && index + 1 < input.Length
                && input[index + 1] is '"' or '\'')
            {
                char valueQuote = input[index + 1];
                valueStart = index + 2;
                valueEnd = FindClosingQuote(
                    input,
                    valueStart,
                    valueQuote,
                    escapedDelimiter: true);
                if (valueEnd < 0)
                {
                    valueEnd = input.Length;
                }
            }
            else if (input[index] is '"' or '\'')
            {
                char valueQuote = input[index];
                valueStart = index + 1;
                valueEnd = FindClosingQuote(
                    input,
                    valueStart,
                    valueQuote,
                    escapedDelimiter: false);
                if (valueEnd < 0)
                {
                    valueEnd = input.Length;
                }
            }
            else if (NormalizeKey(key) == "authorization")
            {
                valueEnd = index;
                while (valueEnd < input.Length
                       && input[valueEnd] is not ('\r' or '\n' or ',' or ';' or '}'))
                {
                    valueEnd++;
                }
            }
            else
            {
                valueEnd = index;
                while (valueEnd < input.Length
                       && !char.IsWhiteSpace(input[valueEnd])
                       && input[valueEnd] is not (',' or ';' or '}' or ']'))
                {
                    valueEnd++;
                }
            }

            if (valueEnd > valueStart
                && !IsSafePlaceholder(input[valueStart..valueEnd]))
            {
                replacements.Add(new(
                    valueStart,
                    valueEnd - valueStart,
                    Placeholder));
            }

            index = Math.Max(valueEnd, keyStart + 1);
        }

        return ApplyReplacements(input, replacements);
    }

    private static int FindClosingQuote(
        string value,
        int start,
        char quote,
        bool escapedDelimiter)
    {
        for (int index = start; index < value.Length; index++)
        {
            if (value[index] != quote)
            {
                continue;
            }

            int backslashes = 0;
            for (int cursor = index - 1;
                 cursor >= start && value[cursor] == '\\';
                 cursor--)
            {
                backslashes++;
            }

            bool closes = escapedDelimiter
                ? backslashes == 1
                : (backslashes & 1) == 0;
            if (closes)
            {
                return index - (escapedDelimiter ? 1 : 0);
            }
        }

        return -1;
    }

    private static bool IsSensitiveQueryKey(string value)
    {
        string normalized = NormalizeKey(value);
        return IsSecretKey(value)
            || normalized is "xamzcredential"
                or "xamzsignature"
                or "xamzsecuritytoken"
                or "xgoogcredential"
                or "xgoogsignature"
                or "awsaccesskeyid"
                or "googleaccessid";
    }

    private static string NormalizeKey(string value)
        => value
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string ReplaceBounded(
        string input,
        Regex regex,
        Func<Match, string> replacement)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var replacements = new List<Replacement>();
        for (int coreStart = 0; coreStart < input.Length; coreStart += ChunkLength)
        {
            int coreEnd = Math.Min(input.Length, coreStart + ChunkLength);
            int windowStart = Math.Max(0, coreStart - OverlapLength);
            int windowEnd = Math.Min(input.Length, coreEnd + OverlapLength);
            string window = input[windowStart..windowEnd];
            try
            {
                foreach (Match match in regex.Matches(window))
                {
                    int globalStart = windowStart + match.Index;
                    if (globalStart < coreStart || globalStart >= coreEnd)
                    {
                        continue;
                    }

                    replacements.Add(new(
                        globalStart,
                        match.Length,
                        replacement(match)));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                replacements.Add(new(coreStart, coreEnd - coreStart, Placeholder));
            }
        }

        return ApplyReplacements(input, replacements);
    }

    private static string ApplyReplacements(
        string input,
        IReadOnlyCollection<Replacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);
        int cursor = 0;
        foreach (Replacement item in replacements.OrderBy(static item => item.Start))
        {
            if (item.Start < cursor)
            {
                continue;
            }

            builder.Append(input, cursor, item.Start - cursor);
            builder.Append(item.Value);
            cursor = item.Start + item.Length;
        }

        builder.Append(input, cursor, input.Length - cursor);
        return builder.ToString();
    }

    private static bool IsJwt(string candidate)
    {
        int separator = candidate.IndexOf('.');
        if (separator < 0)
        {
            return false;
        }

        string header = candidate[..separator].Replace('-', '+').Replace('_', '/');
        header = header.PadRight((header.Length + 3) / 4 * 4, '=');
        try
        {
            byte[] bytes = Convert.FromBase64String(header);
            using JsonDocument document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("alg", out _)
                    || document.RootElement.TryGetProperty("typ", out _));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafePlaceholder(string value)
        => value.Equals("[stored securely]", StringComparison.OrdinalIgnoreCase)
            || value.Equals("[not stored]", StringComparison.OrdinalIgnoreCase)
            || value.Equals(Placeholder, StringComparison.Ordinal)
            || value.Equals("<redacted>", StringComparison.OrdinalIgnoreCase)
            || value.Equals("stored", StringComparison.OrdinalIgnoreCase)
            || value.Equals("not", StringComparison.OrdinalIgnoreCase)
            || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private readonly record struct Replacement(int Start, int Length, string Value);

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9_]{20,255}|github_pat_[A-Za-z0-9_]{20,255})\b",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(
        @"\b[A-Za-z0-9_-]{8,512}\.[A-Za-z0-9_-]{8,4096}\.[A-Za-z0-9_-]{8,1024}\b",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(
        @"\b(authorization\s*[:=]\s*(?:(?:bearer|basic|token)\s+)?)(?:\\?[""'][^""']{0,8192}\\?[""']|[^\s,;]{1,8192})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(
        @"(https?://)[^/\s@]{1,8192}@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex UriUserInfoPattern();

    [GeneratedRegex(
        @"([?&])(?<key>[^=\s&#]{1,1024})=(?<value>[^&\s#]{1,8192})",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex QueryPairPattern();
}
