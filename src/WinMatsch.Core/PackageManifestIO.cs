using System.Text;
using WinMatsch.Core.Yaml;

namespace WinMatsch.Core;

/// <summary>Loads and writes complete multi-file WinGet manifest sets.</summary>
public static class PackageManifestIO
{
    /// <summary>Loads and validates the manifest set in one winget-pkgs version directory.</summary>
    public static PackageManifests LoadDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Manifest directory '{directoryPath}' does not exist.");
        }

        InstallerManifest? installer = null;
        DefaultLocaleManifest? defaultLocale = null;
        VersionManifest? version = null;
        var locales = new List<LocaleManifest>();
        var fileNames = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

        IEnumerable<string> paths = Directory.EnumerateFiles(directoryPath)
            .Where(IsYamlFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (string path in paths)
        {
            string yaml = File.ReadAllText(path);
            ManifestHeader header = ManifestYamlReader.ReadHeader(yaml);
            string typeValue = header.ManifestType
                ?? throw InvalidManifestSet($"Manifest '{Path.GetFileName(path)}' has no ManifestType.");
            _ = new ManifestVersion(header.ManifestVersion
                ?? throw InvalidManifestSet($"Manifest '{Path.GetFileName(path)}' has no ManifestVersion."));

            ManifestType type;
            try
            {
                type = YamlValues.ParseManifestType(typeValue);
            }
            catch (FormatException exception)
            {
                throw InvalidManifestSet(
                    $"Manifest '{Path.GetFileName(path)}' has unsupported ManifestType '{typeValue}'.",
                    exception);
            }

            string fileName = Path.GetFileName(path);
            if (!string.Equals(typeValue, type.ToYaml(), StringComparison.Ordinal))
            {
                throw InvalidManifestSet(
                    $"Manifest '{fileName}' has non-canonical ManifestType '{typeValue}'; "
                    + $"expected '{type.ToYaml()}'.");
            }

            switch (type)
            {
                case ManifestType.Installer:
                    EnsureMissing(installer, ManifestType.Installer);
                    installer = ManifestYamlReader.ReadInstaller(yaml);
                    fileNames.Add(installer, fileName);
                    break;
                case ManifestType.DefaultLocale:
                    EnsureMissing(defaultLocale, ManifestType.DefaultLocale);
                    defaultLocale = ManifestYamlReader.ReadDefaultLocale(yaml);
                    fileNames.Add(defaultLocale, fileName);
                    break;
                case ManifestType.Locale:
                    LocaleManifest locale = ManifestYamlReader.ReadLocale(yaml);
                    locales.Add(locale);
                    fileNames.Add(locale, fileName);
                    break;
                case ManifestType.Version:
                    EnsureMissing(version, ManifestType.Version);
                    version = ManifestYamlReader.ReadVersion(yaml);
                    fileNames.Add(version, fileName);
                    break;
                case ManifestType.Singleton:
                    throw InvalidManifestSet("Singleton manifests cannot be part of a multi-file manifest set.");
                default:
                    throw InvalidManifestSet($"Unsupported manifest type '{type}'.");
            }
        }

        var manifests = new PackageManifests
        {
            Installer = installer ?? throw MissingManifest(ManifestType.Installer),
            DefaultLocale = defaultLocale ?? throw MissingManifest(ManifestType.DefaultLocale),
            Locales = locales,
            Version = version ?? throw MissingManifest(ManifestType.Version),
        };

        Validate(manifests);
        ValidateFileNames(manifests, fileNames);
        return manifests;
    }

    /// <summary>
    /// Serializes a complete manifest set to its canonical winget-pkgs filenames.
    /// Only the bundled schema version can be written, preventing future fields tolerated by the
    /// reader from being silently discarded.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SerializeFiles(
        PackageManifests manifests,
        ManifestWriteOptions? options = null)
    {
        Validate(manifests);
        if (manifests.Version.ManifestVersion != ManifestVersion.Default)
        {
            throw InvalidManifestSet(
                $"Manifest sets can only be written as schema {ManifestVersion.Default.Value}; "
                + $"the set uses {manifests.Version.ManifestVersion.Value}.");
        }

        PackageIdentifier identifier = manifests.Version.PackageIdentifier!;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [ManifestPaths.GetVersionFileName(identifier)] = ManifestYamlWriter.Serialize(manifests.Version, options),
            [ManifestPaths.GetInstallerFileName(identifier)] = ManifestYamlWriter.Serialize(manifests.Installer, options),
            [ManifestPaths.GetLocaleFileName(identifier, manifests.DefaultLocale.PackageLocale!)]
                = ManifestYamlWriter.Serialize(manifests.DefaultLocale, options),
        };

        foreach (LocaleManifest locale in manifests.Locales.OrderBy(static locale => locale.PackageLocale!.Value, StringComparer.Ordinal))
        {
            files.Add(
                ManifestPaths.GetLocaleFileName(identifier, locale.PackageLocale!),
                ManifestYamlWriter.Serialize(locale, options));
        }

        return files;
    }

    /// <summary>
    /// Writes a complete manifest set. Existing unexpected YAML files are rejected so a stale
    /// locale cannot remain in the directory unnoticed.
    /// </summary>
    public static void WriteDirectory(
        string directoryPath,
        PackageManifests manifests,
        ManifestWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        IReadOnlyDictionary<string, string> files = SerializeFiles(manifests, options);
        Directory.CreateDirectory(directoryPath);

        foreach (string path in Directory.EnumerateFiles(directoryPath).Where(IsYamlFile))
        {
            string fileName = Path.GetFileName(path);
            if (!files.ContainsKey(fileName))
            {
                throw new IOException(
                    $"Manifest directory '{directoryPath}' contains unexpected YAML file '{fileName}'.");
            }
        }

        var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string token = Guid.NewGuid().ToString("N");
        var stagedFiles = new List<StagedFile>(files.Count);
        bool completed = false;
        try
        {
            foreach ((string fileName, string yaml) in files)
            {
                string destinationPath = Path.Combine(directoryPath, fileName);
                string stagingPath = Path.Combine(directoryPath, $".{fileName}.{token}.tmp");
                string backupPath = Path.Combine(directoryPath, $".{fileName}.{token}.bak");
                stagedFiles.Add(new StagedFile(destinationPath, stagingPath, backupPath, File.Exists(destinationPath)));
                File.WriteAllText(stagingPath, yaml, utf8WithoutBom);
            }

            foreach (StagedFile file in stagedFiles)
            {
                if (file.HadDestination)
                {
                    File.Replace(file.StagingPath, file.DestinationPath, file.BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(file.StagingPath, file.DestinationPath);
                }

                file.Installed = true;
            }

            completed = true;
        }
        catch (IOException)
        {
            RollBack(stagedFiles);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            RollBack(stagedFiles);
            throw;
        }
        finally
        {
            foreach (StagedFile file in stagedFiles)
            {
                if (File.Exists(file.StagingPath))
                {
                    File.Delete(file.StagingPath);
                }

                if (completed && File.Exists(file.BackupPath))
                {
                    File.Delete(file.BackupPath);
                }
            }
        }
    }

    /// <summary>Validates identity and cross-file invariants for a complete manifest set.</summary>
    public static void Validate(PackageManifests manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(manifests.Installer);
        ArgumentNullException.ThrowIfNull(manifests.DefaultLocale);
        ArgumentNullException.ThrowIfNull(manifests.Locales);
        ArgumentNullException.ThrowIfNull(manifests.Version);

        VersionManifest version = manifests.Version;
        PackageIdentifier identifier = Require(version.PackageIdentifier, "version PackageIdentifier");
        PackageVersion packageVersion = Require(version.PackageVersion, "version PackageVersion");
        LanguageTag defaultLocale = Require(version.DefaultLocale, "version DefaultLocale");
        ManifestVersion schemaVersion = Require(version.ManifestVersion, "version ManifestVersion");
        RequireManifestType(version.ManifestType, ManifestType.Version, "version");

        ValidateCommon(manifests.Installer, "installer", ManifestType.Installer, identifier, packageVersion, schemaVersion);
        ValidateLocale(
            manifests.DefaultLocale,
            "default locale",
            ManifestType.DefaultLocale,
            identifier,
            packageVersion,
            schemaVersion);

        if (!string.Equals(
                Require(manifests.DefaultLocale.PackageLocale, "default locale PackageLocale").Value,
                defaultLocale.Value,
                StringComparison.Ordinal))
        {
            throw InvalidManifestSet(
                $"Default locale manifest locale '{manifests.DefaultLocale.PackageLocale!.Value}' "
                + $"does not match version manifest DefaultLocale '{defaultLocale.Value}'.");
        }

        var seenLocales = new HashSet<LanguageTag> { defaultLocale };
        foreach (LocaleManifest locale in manifests.Locales)
        {
            ArgumentNullException.ThrowIfNull(locale);
            ValidateLocale(locale, "locale", ManifestType.Locale, identifier, packageVersion, schemaVersion);
            LanguageTag packageLocale = Require(locale.PackageLocale, "locale PackageLocale");
            if (!seenLocales.Add(packageLocale))
            {
                throw InvalidManifestSet($"Package locale '{packageLocale.Value}' appears more than once.");
            }
        }
    }

    private static void ValidateLocale(
        LocaleManifest manifest,
        string label,
        ManifestType expectedType,
        PackageIdentifier identifier,
        PackageVersion packageVersion,
        ManifestVersion schemaVersion)
    {
        _ = Require(manifest.PackageLocale, $"{label} PackageLocale");
        ValidateCommon(manifest, label, expectedType, identifier, packageVersion, schemaVersion);
    }

    private static void ValidateCommon(
        object manifest,
        string label,
        ManifestType expectedType,
        PackageIdentifier identifier,
        PackageVersion packageVersion,
        ManifestVersion schemaVersion)
    {
        (PackageIdentifier? actualIdentifier, PackageVersion? actualVersion, ManifestType actualType, ManifestVersion? actualSchemaVersion)
            = manifest switch
            {
                InstallerManifest value => (value.PackageIdentifier, value.PackageVersion, value.ManifestType, value.ManifestVersion),
                LocaleManifest value => (value.PackageIdentifier, value.PackageVersion, value.ManifestType, value.ManifestVersion),
                _ => throw new ArgumentException($"Unsupported manifest model '{manifest.GetType().Name}'.", nameof(manifest)),
            };

        RequireManifestType(actualType, expectedType, label);
        PackageIdentifier manifestIdentifier = Require(actualIdentifier, $"{label} PackageIdentifier");
        if (!string.Equals(manifestIdentifier.Value, identifier.Value, StringComparison.Ordinal))
        {
            throw InvalidManifestSet(
                $"{label} PackageIdentifier '{manifestIdentifier.Value}' does not match '{identifier.Value}'.");
        }

        PackageVersion manifestVersion = Require(actualVersion, $"{label} PackageVersion");
        if (!string.Equals(manifestVersion.Value, packageVersion.Value, StringComparison.Ordinal))
        {
            throw InvalidManifestSet(
                $"{label} PackageVersion '{manifestVersion.Value}' does not match '{packageVersion.Value}'.");
        }

        ManifestVersion manifestSchemaVersion = Require(actualSchemaVersion, $"{label} ManifestVersion");
        if (manifestSchemaVersion != schemaVersion)
        {
            throw InvalidManifestSet(
                $"{label} ManifestVersion '{manifestSchemaVersion.Value}' does not match '{schemaVersion.Value}'.");
        }
    }

    private static void ValidateFileNames(
        PackageManifests manifests,
        Dictionary<object, string> fileNames)
    {
        PackageIdentifier identifier = manifests.Version.PackageIdentifier!;
        ValidateFileName(
            fileNames[manifests.Version],
            ManifestPaths.GetVersionFileName(identifier),
            ManifestType.Version);
        ValidateFileName(
            fileNames[manifests.Installer],
            ManifestPaths.GetInstallerFileName(identifier),
            ManifestType.Installer);
        ValidateFileName(
            fileNames[manifests.DefaultLocale],
            ManifestPaths.GetLocaleFileName(identifier, manifests.DefaultLocale.PackageLocale!),
            ManifestType.DefaultLocale);

        foreach (LocaleManifest locale in manifests.Locales)
        {
            ValidateFileName(
                fileNames[locale],
                ManifestPaths.GetLocaleFileName(identifier, locale.PackageLocale!),
                ManifestType.Locale);
        }
    }

    private static void ValidateFileName(string actual, string expected, ManifestType type)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw InvalidManifestSet(
                $"{type.ToYaml()} manifest filename '{actual}' must be '{expected}'.");
        }
    }

    private static void RequireManifestType(ManifestType actual, ManifestType expected, string label)
    {
        if (actual != expected)
        {
            throw InvalidManifestSet(
                $"{label} manifest has ManifestType '{actual.ToYaml()}', expected '{expected.ToYaml()}'.");
        }
    }

    private static T Require<T>(T? value, string field)
        where T : class
        => value ?? throw InvalidManifestSet($"The {field} is required.");

    private static void EnsureMissing<T>(T? manifest, ManifestType type)
        where T : class
    {
        if (manifest is not null)
        {
            throw InvalidManifestSet($"The manifest set contains more than one {type.ToYaml()} manifest.");
        }
    }

    private static InvalidDataException MissingManifest(ManifestType type)
        => InvalidManifestSet($"The manifest set does not contain a {type.ToYaml()} manifest.");

    private static InvalidDataException InvalidManifestSet(string message, Exception? innerException = null)
        => new($"Invalid WinGet manifest set: {message}", innerException);

    private static bool IsYamlFile(string path)
        => Path.GetExtension(path) is { } extension
            && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));

    private static void RollBack(IReadOnlyList<StagedFile> files)
    {
        for (int i = files.Count - 1; i >= 0; i--)
        {
            StagedFile file = files[i];
            if (!file.Installed)
            {
                continue;
            }

            if (file.HadDestination)
            {
                File.Replace(file.BackupPath, file.DestinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Delete(file.DestinationPath);
            }
        }
    }

    private sealed class StagedFile(
        string destinationPath,
        string stagingPath,
        string backupPath,
        bool hadDestination)
    {
        public string DestinationPath { get; } = destinationPath;

        public string StagingPath { get; } = stagingPath;

        public string BackupPath { get; } = backupPath;

        public bool HadDestination { get; } = hadDestination;

        public bool Installed { get; set; }
    }
}
