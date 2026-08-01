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
