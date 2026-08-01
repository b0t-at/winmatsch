using WinMatsch.Core;

namespace WinMatsch.Workflows.Mapping;

/// <summary>An explicit URL mapping in <c>url|arch|scope|displayVersion</c> form.</summary>
public sealed record UrlOverride(
    Uri Url,
    Architecture Architecture,
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
        if (parts.Length != 4)
        {
            error = "A URL override must use url|arch|scope|displayVersion syntax.";
            return false;
        }

        if (!Uri.TryCreate(parts[0].Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "The URL override URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!TryParseArchitecture(parts[1], out Architecture architecture))
        {
            error = $"Unknown architecture '{parts[1]}'.";
            return false;
        }

        Scope? scope = parts[2].Trim().ToLowerInvariant() switch
        {
            "" => null,
            "user" => WinMatsch.Core.Scope.User,
            "machine" => WinMatsch.Core.Scope.Machine,
            _ => null,
        };
        if (parts[2].Length > 0 && scope is null)
        {
            error = $"Unknown scope '{parts[2]}'.";
            return false;
        }

        string? displayVersion = string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3].Trim();
        result = new(uri, architecture, scope, displayVersion);
        return true;
    }

    private static bool TryParseArchitecture(string value, out Architecture architecture)
    {
        architecture = value.Trim().ToLowerInvariant() switch
        {
            "x86" => Architecture.X86,
            "x64" => Architecture.X64,
            "arm" => Architecture.Arm,
            "arm64" => Architecture.Arm64,
            "neutral" => Architecture.Neutral,
            _ => (Architecture)(-1),
        };
        return Enum.IsDefined(architecture);
    }
}
