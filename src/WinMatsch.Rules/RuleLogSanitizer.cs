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

        return SanitizeText(value);
    }

    public static string SanitizeMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SanitizeText(message);
    }

    private static string SanitizeText(string value)
    {
        if (TryDecodeSensitiveText(value, out string decoded))
        {
            value = decoded;
        }

        var result = new System.Text.StringBuilder(value.Length);
        int position = 0;
        while (position < value.Length)
        {
            int start = FindNextUriStart(value, position);
            if (start < 0)
            {
                result.Append(value, position, value.Length - position);
                break;
            }

            result.Append(value, position, start - position);
            int end = start;
            while (end < value.Length && !IsUriTerminator(value[end]))
            {
                end++;
            }

            string candidate = value[start..end];
            if (TrySanitizeUri(candidate, out string? sanitizedUri))
            {
                result.Append(sanitizedUri);
            }
            else
            {
                result.Append(candidate);
            }

            position = end;
        }

        string sanitized = result.ToString();
        return ContainsBoundedCredential(sanitized) ? "[REDACTED]" : sanitized;
    }

    private static int FindNextUriStart(string value, int position)
    {
        int separator = value.IndexOf("://", position, StringComparison.Ordinal);
        while (separator >= 0)
        {
            int start = separator;
            while (start > position && IsSchemeCharacter(value[start - 1]))
            {
                start--;
            }

            if (start < separator && char.IsAsciiLetter(value[start]))
            {
                return start;
            }

            separator = value.IndexOf("://", separator + 3, StringComparison.Ordinal);
        }

        return -1;
    }

    private static bool IsSchemeCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '+' or '-' or '.';

    private static bool TryDecodeSensitiveText(string value, out string decoded)
    {
        string current = value;
        const int maximumDecodeIterations = 5;
        for (int iteration = 0; iteration < maximumDecodeIterations; iteration++)
        {
            string next = Uri.UnescapeDataString(current);
            if (string.Equals(next, current, StringComparison.Ordinal))
            {
                break;
            }

            current = next;
            if (current.Length > 65_536)
            {
                decoded = "[REDACTED]";
                return true;
            }

            if (iteration == maximumDecodeIterations - 1 && current.Contains('%'))
            {
                decoded = "[REDACTED]";
                return true;
            }
        }

        if (!string.Equals(current, value, StringComparison.Ordinal)
            && (current.Contains("http://", StringComparison.OrdinalIgnoreCase)
                || current.Contains("https://", StringComparison.OrdinalIgnoreCase)
                || ContainsBoundedCredential(current)))
        {
            decoded = current;
            return true;
        }

        decoded = null!;
        return false;
    }

    private static bool TrySanitizeUri(string value, out string? sanitized)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            sanitized = "[REDACTED]";
            return true;
        }

        string decodedPath = uri.AbsolutePath;
        const int maximumDecodeIterations = 5;
        for (int iteration = 0; iteration < maximumDecodeIterations; iteration++)
        {
            string next = Uri.UnescapeDataString(decodedPath);
            if (string.Equals(next, decodedPath, StringComparison.Ordinal))
            {
                break;
            }

            decodedPath = next;
            if (decodedPath.Length > 65_536)
            {
                sanitized = "[REDACTED]";
                return true;
            }

            if (iteration == maximumDecodeIterations - 1
                && decodedPath.Contains('%'))
            {
                sanitized = "[REDACTED]";
                return true;
            }
        }

        if (ContainsBoundedCredential(decodedPath))
        {
            sanitized = "[REDACTED]";
            return true;
        }

        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        sanitized = safe.Uri.AbsoluteUri;
        return true;
    }

    private static bool ContainsBoundedCredential(string value)
    {
        ReadOnlySpan<string> assignmentNames =
        [
            "token",
            "access_token",
            "refresh_token",
            "id_token",
            "api_token",
            "oauth_token",
            "client_secret",
            "oauth_client_secret",
            "access-token",
            "refresh-token",
            "id-token",
            "api-token",
            "oauth-token",
            "client-secret",
            "oauth-client-secret",
            "accessToken",
            "refreshToken",
            "idToken",
            "apiToken",
            "oauthToken",
            "clientSecret",
            "oauthClientSecret",
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
                    if (next < value.Length && value[next] is '\'' or '"')
                    {
                        next++;
                    }

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

        return ContainsBearerToken(value)
            || ContainsBasicCredentials(value)
            || LooksLikeJwt(value);
    }

    private static bool IsUriTerminator(char value)
        => char.IsWhiteSpace(value) || value is ')' or ']' or '}' or '\'' or '"';

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static bool ContainsBearerToken(string value)
    {
        foreach (ReadOnlyMemory<char> token in FindAuthorizationTokens(value, "bearer"))
        {
            if (token.Length > 0 && IsAuthorizationToken(token.Span))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBasicCredentials(string value)
    {
        foreach (ReadOnlyMemory<char> token in FindAuthorizationTokens(value, "basic"))
        {
            string encoded = token.ToString();
            int maximumDecodedLength = ((encoded.Length + 3) / 4) * 3;
            byte[] decoded = new byte[maximumDecodedLength];
            if (Convert.TryFromBase64String(encoded, decoded, out int bytesWritten)
                && decoded.AsSpan(0, bytesWritten).Contains((byte)':'))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ReadOnlyMemory<char>> FindAuthorizationTokens(string value, string scheme)
    {
        int searchStart = 0;
        while (searchStart < value.Length)
        {
            int index = value.IndexOf(scheme, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                yield break;
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

                yield return value.AsMemory(tokenStart, tokenEnd - tokenStart);
            }

            searchStart = end;
        }
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
