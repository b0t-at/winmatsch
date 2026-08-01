using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>Small shared helpers for the policy catalogue rules. Pure functions only.</summary>
internal static partial class PolicyValues
{
    /// <summary>All locale manifests of a run: the default locale first, then the extra locales.</summary>
    public static IEnumerable<(LocaleManifest Locale, string DocumentName)> EnumerateLocales(
        PackageManifests manifests)
    {
        yield return (manifests.DefaultLocale, "DefaultLocale");
        for (int i = 0; i < manifests.Locales.Count; i++)
        {
            yield return (manifests.Locales[i], $"Locales[{i}]");
        }
    }

    /// <summary>
    /// The repository file path of a locale manifest, mirroring the fallbacks
    /// <c>ManifestSnapshot</c> uses so change evidence keys line up with diff paths.
    /// </summary>
    public static string GetLocaleManifestPath(PackageManifests manifests, LocaleManifest locale)
    {
        if (ReferenceEquals(locale, manifests.DefaultLocale))
        {
            return locale.PackageIdentifier is { } identifier && locale.PackageLocale is { } language
                ? ManifestPaths.GetLocaleFileName(identifier, language)
                : "defaultLocale.yaml";
        }

        int index = manifests.Locales.IndexOf(locale);
        return locale.PackageIdentifier is { } localeIdentifier && locale.PackageLocale is { } localeLanguage
            ? ManifestPaths.GetLocaleFileName(localeIdentifier, localeLanguage)
            : $"locale[{index}].yaml";
    }

    /// <summary>The repository file path of the version manifest, mirroring <c>ManifestSnapshot</c>.</summary>
    public static string GetVersionManifestPath(PackageManifests manifests)
        => manifests.Version.PackageIdentifier is { } identifier
            ? ManifestPaths.GetVersionFileName(identifier)
            : "version.yaml";

    /// <summary>
    /// True when <paramref name="value"/> contains <paramref name="version"/> as a delimited
    /// version token: the neighboring characters must not be digits or dots, so an old
    /// version <c>1.2</c> never matches inside <c>11.2.0</c>.
    /// </summary>
    public static bool ContainsVersionToken(string value, string version)
        => IndexOfVersionToken(value, version, 0) >= 0;

    /// <summary>Replaces every delimited occurrence of <paramref name="version"/> (see <see cref="ContainsVersionToken"/>).</summary>
    public static string ReplaceVersionToken(string value, string version, string replacement)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        int position = 0;
        while (true)
        {
            int index = IndexOfVersionToken(value, version, position);
            if (index < 0)
            {
                builder.Append(value, position, value.Length - position);
                return builder.ToString();
            }

            builder.Append(value, position, index - position).Append(replacement);
            position = index + version.Length;
        }
    }

    private static int IndexOfVersionToken(string value, string version, int start)
    {
        while (start <= value.Length - version.Length)
        {
            int index = value.IndexOf(version, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            bool leftOk = index == 0 || !IsVersionChar(value[index - 1]);
            int end = index + version.Length;
            bool rightOk = end == value.Length || !IsVersionChar(value[end]);
            if (leftOk && rightOk)
            {
                return index;
            }

            start = index + 1;
        }

        return -1;
    }

    private static bool IsVersionChar(char c) => char.IsAsciiDigit(c) || c == '.';

    /// <summary>
    /// Finds the previous installer matching the given effective Architecture+InstallerType+Scope
    /// key, or null when none or several match.
    /// </summary>
    public static Installer? FindPreviousByEntryKey(
        InstallerManifest currentManifest,
        Installer current,
        InstallerManifest previousManifest)
    {
        if (previousManifest.Installers is not { } previousInstallers)
        {
            return null;
        }

        string key = EffectiveInstallerValues.GetEntryKey(currentManifest, current);
        Installer? match = null;
        foreach (Installer candidate in previousInstallers)
        {
            if (string.Equals(EffectiveInstallerValues.GetEntryKey(previousManifest, candidate), key, StringComparison.Ordinal))
            {
                if (match is not null)
                {
                    return null;
                }

                match = candidate;
            }
        }

        return match;
    }

    /// <summary>True when the value contains a version-looking token (digits separated by dots).</summary>
    public static bool ContainsVersionLikeToken(string value) => VersionToken().IsMatch(value);

    /// <summary>True when the string contains a control or otherwise non-printable character.</summary>
    public static bool ContainsNonPrintable(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c) || c == '\uFFFD')
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionToken();
}
