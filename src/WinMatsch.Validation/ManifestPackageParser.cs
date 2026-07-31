using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using YamlDotNet.Core;

namespace WinMatsch.Validation;

internal static class ManifestPackageParser
{
    public static ParsedPackage? Parse(
        IReadOnlyList<ManifestDocument> documents,
        List<ValidationFinding> findings)
    {
        if (documents.Count == 0)
        {
            findings.Add(Error("VLD2001", "The package manifest set is empty."));
            return null;
        }

        InstallerManifest? installer = null;
        DefaultLocaleManifest? defaultLocale = null;
        VersionManifest? version = null;
        var locales = new List<LocaleManifest>();
        var parsedDocuments = new List<ParsedManifestDocument>();

        foreach (ManifestDocument document in documents.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            ManifestHeader header;
            try
            {
                header = ManifestYamlReader.ReadHeader(document.Content);
            }
            catch (YamlException exception)
            {
                findings.Add(Error("VLD1001", $"Invalid YAML: {exception.Message}", document.RepositoryPath));
                continue;
            }

            ManifestType? type = ParseType(header.ManifestType);
            if (type is null || type == ManifestType.Singleton)
            {
                findings.Add(Error(
                    "VLD2002",
                    $"ManifestType '{header.ManifestType ?? "<missing>"}' is not valid in a multi-file package set.",
                    document.RepositoryPath));
                continue;
            }

            ValidateSchemaHeader(document, type.Value, findings);
            findings.AddRange(ManifestSchemaValidator.Validate(document, type.Value).Findings);
            if (!string.Equals(header.ManifestType, type.Value.ToYaml(), StringComparison.Ordinal))
            {
                findings.Add(Error(
                    "VLD2101",
                    $"ManifestType must use exact value '{type.Value.ToYaml()}'.",
                    document.RepositoryPath));
            }

            if (!string.Equals(
                    header.ManifestVersion,
                    ManifestSchemaValidator.SchemaVersion,
                    StringComparison.Ordinal))
            {
                findings.Add(Error(
                    "VLD2102",
                    $"ManifestVersion must be exactly '{ManifestSchemaValidator.SchemaVersion}'.",
                    document.RepositoryPath));
            }

            try
            {
                object manifest = type.Value switch
                {
                    ManifestType.Installer => ManifestYamlReader.ReadInstaller(document.Content),
                    ManifestType.DefaultLocale => ManifestYamlReader.ReadDefaultLocale(document.Content),
                    ManifestType.Locale => ManifestYamlReader.ReadLocale(document.Content),
                    ManifestType.Version => ManifestYamlReader.ReadVersion(document.Content),
                    _ => throw new InvalidOperationException($"Unsupported manifest type '{type.Value}'."),
                };

                parsedDocuments.Add(new ParsedManifestDocument(document, type.Value, manifest));
                switch (manifest)
                {
                    case InstallerManifest value when installer is null:
                        installer = value;
                        break;
                    case InstallerManifest:
                        findings.Add(DuplicateType(type.Value, document.RepositoryPath));
                        break;
                    case DefaultLocaleManifest value when defaultLocale is null:
                        defaultLocale = value;
                        break;
                    case DefaultLocaleManifest:
                        findings.Add(DuplicateType(type.Value, document.RepositoryPath));
                        break;
                    case VersionManifest value when version is null:
                        version = value;
                        break;
                    case VersionManifest:
                        findings.Add(DuplicateType(type.Value, document.RepositoryPath));
                        break;
                    case LocaleManifest value:
                        locales.Add(value);
                        break;
                }
            }
            catch (YamlException exception)
            {
                findings.Add(Error("VLD2003", $"Manifest could not be read: {exception.Message}", document.RepositoryPath));
            }
            catch (FormatException exception)
            {
                findings.Add(Error("VLD2003", $"Manifest contains an invalid value: {exception.Message}", document.RepositoryPath));
            }
            catch (ArgumentException exception)
            {
                findings.Add(Error("VLD2003", $"Manifest contains an invalid value: {exception.Message}", document.RepositoryPath));
            }
        }

        RequireManifest(installer, ManifestType.Installer, findings);
        RequireManifest(defaultLocale, ManifestType.DefaultLocale, findings);
        RequireManifest(version, ManifestType.Version, findings);
        if (installer is null || defaultLocale is null || version is null)
        {
            return null;
        }

        var manifests = new PackageManifests
        {
            Installer = installer,
            DefaultLocale = defaultLocale,
            Locales = locales,
            Version = version,
        };
        try
        {
            PackageManifestIO.Validate(manifests);
        }
        catch (InvalidDataException exception)
        {
            findings.Add(Error("VLD2103", exception.Message));
        }

        return new ParsedPackage(manifests, parsedDocuments);
    }

    private static ManifestType? ParseType(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return YamlValues.ParseManifestType(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void ValidateSchemaHeader(
        ManifestDocument document,
        ManifestType type,
        List<ValidationFinding> findings)
    {
        const string prefix = "# yaml-language-server: $schema=";
        string expected = $"{prefix}https://aka.ms/winget-manifest.{type.ToYaml()}."
            + $"{ManifestSchemaValidator.SchemaVersion}.schema.json";
        string? actual = document.Content
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            findings.Add(Error(
                "VLD2104",
                actual is null
                    ? $"Manifest must include exact schema header '{expected}'."
                    : $"Manifest schema header must be exactly '{expected}'.",
                document.RepositoryPath));
        }
    }

    private static void RequireManifest<T>(
        T? manifest,
        ManifestType type,
        List<ValidationFinding> findings)
        where T : class
    {
        if (manifest is null)
        {
            findings.Add(Error(
                "VLD2004",
                $"The package set must contain exactly one {type.ToYaml()} manifest."));
        }
    }

    private static ValidationFinding DuplicateType(ManifestType type, string path)
        => Error(
            "VLD2005",
            $"The package set contains more than one {type.ToYaml()} manifest.",
            path);

    private static ValidationFinding Error(string code, string message, string? path = null)
        => new(code, ValidationSeverity.Error, message, path);
}

internal sealed record ParsedManifestDocument(
    ManifestDocument Document,
    ManifestType Type,
    object Manifest);

internal sealed record ParsedPackage(
    PackageManifests Manifests,
    IReadOnlyList<ParsedManifestDocument> Documents);
