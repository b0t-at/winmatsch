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
            string sanitizedUri = safe.Uri.AbsoluteUri;
            return ContainsBoundedCredential(sanitizedUri) ? "[REDACTED]" : sanitizedUri;
        }

        return ContainsBoundedCredential(value) ? "[REDACTED]" : value;
    }

    public static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
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

        string sanitized = result.ToString();
        return ContainsBoundedCredential(sanitized) ? "[REDACTED]" : sanitized;
    }

    private static bool ContainsBoundedCredential(string value)
    {
        ReadOnlySpan<string> assignmentNames =
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
            "session",
            "cookie",
        ];
        foreach (string name in assignmentNames)
        {
            int searchStart = 0;
            while (searchStart < value.Length)
            {
                int index = value.IndexOf(name, searchStart, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                int end = index + name.Length;
                bool boundedBefore = index == 0 || !IsIdentifierCharacter(value[index - 1]);
                bool boundedAfter = end == value.Length || !IsIdentifierCharacter(value[end]);
                if (boundedBefore && boundedAfter)
                {
                    int next = end;
                    while (next < value.Length && char.IsWhiteSpace(value[next]))
                    {
                        next++;
                    }

                    if (next < value.Length && value[next] is ':' or '=')
                    {
                        return true;
                    }

                    bool commandSwitch = index >= 2 && value[index - 2] == '-' && value[index - 1] == '-'
                        || index >= 1 && value[index - 1] == '/';
                    if (commandSwitch && next > end && next < value.Length)
                    {
                        return true;
                    }
                }

                searchStart = end;
            }
        }

        return ContainsAuthorizationScheme(value, "bearer")
            || ContainsAuthorizationScheme(value, "basic")
            || LooksLikeJwt(value);
    }

    private static bool IsUriTerminator(char value)
        => char.IsWhiteSpace(value) || value is ')' or ']' or '}' or ',' or ';' or '\'' or '"';

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static bool ContainsAuthorizationScheme(string value, string scheme)
    {
        int searchStart = 0;
        while (searchStart < value.Length)
        {
            int index = value.IndexOf(scheme, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int end = index + scheme.Length;
            bool boundedBefore = index == 0 || !IsIdentifierCharacter(value[index - 1]);
            if (boundedBefore && end < value.Length && char.IsWhiteSpace(value[end]))
            {
                int tokenStart = end;
                while (tokenStart < value.Length && char.IsWhiteSpace(value[tokenStart]))
                {
                    tokenStart++;
                }

                int tokenEnd = tokenStart;
                while (tokenEnd < value.Length
                    && !char.IsWhiteSpace(value[tokenEnd])
                    && !IsUriTerminator(value[tokenEnd]))
                {
                    tokenEnd++;
                }

                ReadOnlySpan<char> token = value.AsSpan(tokenStart, tokenEnd - tokenStart);
                if (token.Length >= 12 && IsAuthorizationToken(token))
                {
                    return true;
                }
            }

            searchStart = end;
        }

        return false;
    }

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

    private static bool IsAuthorizationTokenCharacter(char value)
        => IsBase64UrlCharacter(value) || value is '.' or '~' or '+' or '/';

    private static bool IsBase64UrlToken(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!IsBase64UrlCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAuthorizationToken(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!IsAuthorizationTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }
}
