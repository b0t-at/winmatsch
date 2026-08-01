using WinMatsch.Core;

namespace WinMatsch.Workflows.Mapping;

/// <summary>
/// An explicit URL mapping in <c>url</c>, <c>url|arch</c>,
/// <c>url|arch|scope</c>, or <c>url|arch|scope|displayVersion</c> form.
/// </summary>
public sealed record UrlOverride(
    Uri Url,
    Architecture? Architecture,
    Scope? Scope,
    string? DisplayVersion)
{
    public static UrlOverride Parse(string value)
    {
        if (!TryParse(value, out UrlOverride? result, out string? error))
        {
            throw new FormatException(error);
        }

        return result!;
    }

    public static bool TryParse(string? value, out UrlOverride? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A URL override must not be empty.";
            return false;
        }

        string[] parts = value.Split('|', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4)
        {
            error = "A URL override must use url, url|arch, url|arch|scope, or url|arch|scope|displayVersion syntax.";
            return false;
        }

        if (!Uri.TryCreate(parts[0].Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "The URL override URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        Architecture? architecture = null;
        if (parts.Length >= 2)
        {
            if (string.IsNullOrWhiteSpace(parts[1]))
            {
                error = "The architecture component of a URL override must not be empty.";
                return false;
            }

            if (!TryParseArchitecture(parts[1], out Architecture parsedArchitecture))
            {
                error = $"Unknown architecture '{parts[1]}'.";
                return false;
            }

            architecture = parsedArchitecture;
        }

        Scope? scope = null;
        if (parts.Length >= 3)
        {
            if (string.IsNullOrWhiteSpace(parts[2]))
            {
                error = "The scope component of a URL override must not be empty.";
                return false;
            }

            scope = parts[2].Trim().ToLowerInvariant() switch
            {
                "user" => WinMatsch.Core.Scope.User,
                "machine" => WinMatsch.Core.Scope.Machine,
                _ => null,
            };
            if (scope is null)
            {
                error = $"Unknown scope '{parts[2]}'.";
                return false;
            }
        }

        string? displayVersion = null;
        if (parts.Length == 4)
        {
            if (string.IsNullOrWhiteSpace(parts[3]))
            {
                error = "The displayVersion component of a URL override must not be empty.";
                return false;
            }

            displayVersion = parts[3].Trim();
        }

        result = new(uri, architecture, scope, displayVersion);
        return true;
    }

    private static bool TryParseArchitecture(string value, out Architecture architecture)
    {
        architecture = value.Trim().ToLowerInvariant() switch
        {
            "x86" => WinMatsch.Core.Architecture.X86,
            "x64" => WinMatsch.Core.Architecture.X64,
            "arm" => WinMatsch.Core.Architecture.Arm,
            "arm64" => WinMatsch.Core.Architecture.Arm64,
            "neutral" => WinMatsch.Core.Architecture.Neutral,
            _ => (WinMatsch.Core.Architecture)(-1),
        };
        return Enum.IsDefined(architecture);
    }
}
