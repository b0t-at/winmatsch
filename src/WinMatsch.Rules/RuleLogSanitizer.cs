namespace WinMatsch.Rules;

internal static class RuleLogSanitizer
{
    private static readonly string[] _sensitivePathTerms =
    [
        "password",
        "passwd",
        "secret",
        "credential",
        "accessToken",
        "refreshToken",
        "authorization",
        "apiKey",
        "installerSwitches",
    ];

    public static string? Sanitize(string fieldPath, string? value)
    {
        if (value is null)
        {
            return null;
        }

        foreach (string term in _sensitivePathTerms)
        {
            if (fieldPath.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return "[REDACTED]";
            }
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            var safe = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return safe.Uri.AbsoluteUri;
        }

        return ContainsCredentialMaterial(value) ? "[REDACTED]" : value;
    }

    public static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (ContainsCredentialMaterial(message))
        {
            return "[REDACTED]";
        }

        var result = new System.Text.StringBuilder(message.Length);
        int position = 0;
        while (position < message.Length)
        {
            int http = message.IndexOf("http://", position, StringComparison.OrdinalIgnoreCase);
            int https = message.IndexOf("https://", position, StringComparison.OrdinalIgnoreCase);
            int start = http < 0 ? https : https < 0 ? http : Math.Min(http, https);
            if (start < 0)
            {
                result.Append(message, position, message.Length - position);
                break;
            }

            result.Append(message, position, start - position);
            int end = start;
            while (end < message.Length && !IsUriTerminator(message[end]))
            {
                end++;
            }

            string candidate = message[start..end];
            result.Append(Sanitize(string.Empty, candidate));
            position = end;
        }

        return result.ToString();
    }

    private static bool ContainsCredentialMaterial(string value)
    {
        ReadOnlySpan<string> names =
        [
            "token",
            "password",
            "passwd",
            "secret",
            "api-key",
            "apikey",
            "api_key",
            "authorization",
            "credential",
            "bearer ",
            "basic ",
            "session",
            "cookie",
        ];
        foreach (string name in names)
        {
            if (value.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return LooksLikeJwt(value);
    }

    private static bool IsUriTerminator(char value)
        => char.IsWhiteSpace(value) || value is ')' or ']' or '}' or ',' or ';' or '\'' or '"';

    private static bool LooksLikeJwt(string value)
    {
        foreach (string candidate in value.Split(
                     [' ', '\t', '\r', '\n', ',', ';', '(', ')', '[', ']', '{', '}', '"', '\''],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] segments = candidate.Split('.');
            if (segments.Length == 3
                && segments.All(static segment => segment.Length >= 6 && segment.All(IsBase64UrlCharacter)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBase64UrlCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '=';
}
