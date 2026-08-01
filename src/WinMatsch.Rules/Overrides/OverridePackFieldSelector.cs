using System.Text.RegularExpressions;

namespace WinMatsch.Rules.OverridePacks;

internal static partial class OverridePackFieldSelector
{
    private static readonly HashSet<string> _localeFields = new(
        [
            "Publisher",
            "PublisherUrl",
            "PublisherSupportUrl",
            "PrivacyUrl",
            "Author",
            "PackageName",
            "PackageUrl",
            "License",
            "LicenseUrl",
            "Copyright",
            "CopyrightUrl",
            "ShortDescription",
            "Description",
            "Tags",
            "Agreements",
            "ReleaseNotes",
            "ReleaseNotesUrl",
            "PurchaseUrl",
            "InstallationNotes",
            "Documentations",
            "Icons",
            "Moniker",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> _installerFields = new(
        [
            "InstallerLocale",
            "Platform",
            "MinimumOSVersion",
            "InstallerType",
            "NestedInstallerType",
            "NestedInstallerFiles",
            "Scope",
            "InstallModes",
            "InstallerSwitches",
            "InstallerSuccessCodes",
            "ExpectedReturnCodes",
            "UpgradeBehavior",
            "Commands",
            "Protocols",
            "FileExtensions",
            "Dependencies",
            "PackageFamilyName",
            "ProductCode",
            "Capabilities",
            "RestrictedCapabilities",
            "Markets",
            "InstallerAbortsTerminal",
            "ReleaseDate",
            "InstallLocationRequired",
            "RequireExplicitUpgrade",
            "DisplayInstallWarnings",
            "UnsupportedOSArchitectures",
            "UnsupportedArguments",
            "AppsAndFeaturesEntries",
            "ElevationRequirement",
            "InstallationMetadata",
            "DownloadCommandProhibited",
            "RepairBehavior",
            "ArchiveBinariesDependOnPath",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> _learnedInstallerFields = new(
        ["Architecture", "InstallerType", "NestedInstallerType", "Scope", "InstallerLocale"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> _learnedLocaleFields = new(
        [
            "Publisher",
            "PublisherUrl",
            "PublisherSupportUrl",
            "PrivacyUrl",
            "Author",
            "PackageName",
            "PackageUrl",
            "License",
            "LicenseUrl",
            "Copyright",
            "CopyrightUrl",
            "ShortDescription",
            "Description",
            "ReleaseNotes",
            "ReleaseNotesUrl",
            "PurchaseUrl",
            "InstallationNotes",
            "Moniker",
        ],
        StringComparer.Ordinal);

    public static void ValidateSelector(string selector, string description)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new FormatException($"'{description}' must contain a non-empty field selector.");
        }

        string value = selector.Trim();
        if (value.StartsWith("DefaultLocale.", StringComparison.Ordinal))
        {
            ValidateField(value["DefaultLocale.".Length..], _localeFields, description);
            return;
        }

        if (value.StartsWith("Locales[*].", StringComparison.Ordinal))
        {
            ValidateField(value["Locales[*].".Length..], _localeFields, description);
            return;
        }

        if (value.StartsWith("Installer.", StringComparison.Ordinal))
        {
            ValidateField(value["Installer.".Length..], _installerFields, description);
            return;
        }

        if (value.StartsWith("Installers[*].", StringComparison.Ordinal))
        {
            ValidateField(value["Installers[*].".Length..], _installerFields, description);
            return;
        }

        if (_localeFields.Contains(value) || _installerFields.Contains(value))
        {
            return;
        }

        throw new FormatException(
            $"'{description}' selector '{selector}' is unsupported. Use DefaultLocale.<field>, Locales[*].<field>, Installer.<field>, or Installers[*].<field>.");
    }

    public static void ValidateMetadataUrlReplacement(string source, string replacement)
    {
        if (!TrySafeMetadataUri(source, requireHttps: false, out _))
        {
            throw new FormatException(
                "A metadataUrlReplacements source must be an exact HTTP or HTTPS URL without credentials, query, or fragment.");
        }

        if (!TrySafeMetadataUri(replacement, requireHttps: true, out _))
        {
            throw new FormatException(
                "A metadataUrlReplacements target must be an exact HTTPS URL without credentials, query, or fragment.");
        }
    }

    public static void ValidateLearned(LearnedFieldOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool installer = string.Equals(value.DocumentKey, "installer", StringComparison.Ordinal);
        bool locale = string.Equals(value.DocumentKey, "defaultLocale", StringComparison.Ordinal);
        if (!installer && !locale)
        {
            throw new FormatException(
                $"learnedFields.documentKey '{value.DocumentKey}' is unsupported; only installer and defaultLocale corrections can be learned safely.");
        }

        string field = value.SemanticPath.Split('.').Last();
        if (installer ? !_learnedInstallerFields.Contains(field) : !_learnedLocaleFields.Contains(field))
        {
            throw new FormatException(
                $"learnedFields semantic path '{value.SemanticPath}' targets a field that cannot be learned safely.");
        }

        if (!Sha256Regex().IsMatch(value.ValueSha256)
            || !Sha256Regex().IsMatch(value.BotValueSha256)
            || !Sha256Regex().IsMatch(value.SourceFingerprint))
        {
            throw new FormatException("learnedFields hashes must be 64 hexadecimal SHA-256 characters.");
        }

        bool installerEntry = installer
            && value.SemanticPath.StartsWith("Installers{installer:", StringComparison.Ordinal);
        if (installerEntry
            && (value.InstallerSelectorSha256 is null
                || !Sha256Regex().IsMatch(value.InstallerSelectorSha256)))
        {
            throw new FormatException(
                "Installer learnedFields require a stable 64-character installerSelectorSha256.");
        }

        if (!installerEntry && value.InstallerSelectorSha256 is not null)
        {
            throw new FormatException(
                "Only per-installer learnedFields may declare installerSelectorSha256.");
        }

        if (_learnedLocaleFields.Contains(field)
            && field.EndsWith("Url", StringComparison.Ordinal)
            && !TrySafeMetadataUri(value.Value, requireHttps: true, out _))
        {
            throw new FormatException(
                $"learnedFields value for '{field}' must be an exact safe HTTPS URL.");
        }
    }

    public static bool TrySafeMetadataUri(string value, bool requireHttps, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || (requireHttps
                ? !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                : !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && !parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static void ValidateField(
        string field,
        HashSet<string> supported,
        string description)
    {
        if (!supported.Contains(field))
        {
            throw new FormatException($"'{description}' targets unsupported field '{field}'.");
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
