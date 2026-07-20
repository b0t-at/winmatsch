using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Core.Yaml;

/// <summary>
/// Deserializes WinGet manifest YAML into the typed model by walking YamlDotNet's representation
/// model with explicit, hand-written mappings. No reflection is involved, which keeps reading
/// trimmer/AOT-safe and makes the accepted shape of every field explicit.
/// Unknown keys are ignored so newer schema versions never break reading; scalar values go
/// through the same strict primitives and enum maps used by the rest of the tool.
/// </summary>
public static class ManifestYamlReader
{
    /// <summary>Reads only the common header fields, without validating them.</summary>
    public static ManifestHeader ReadHeader(string yaml)
    {
        MappingReader reader = LoadRoot(yaml);
        return new ManifestHeader
        {
            PackageIdentifier = reader.String("PackageIdentifier"),
            PackageVersion = reader.String("PackageVersion"),
            ManifestType = reader.String("ManifestType"),
            ManifestVersion = reader.String("ManifestVersion"),
        };
    }

    /// <summary>Detects the manifest type from the <c>ManifestType</c> field, or null when absent or unknown.</summary>
    public static ManifestType? TryDetectType(string yaml)
    {
        try
        {
            string? type = ReadHeader(yaml).ManifestType;
            return type is null ? null : YamlValues.ParseManifestType(type);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static InstallerManifest ReadInstaller(string yaml)
    {
        MappingReader reader = LoadRoot(yaml);
        var manifest = new InstallerManifest
        {
            PackageIdentifier = reader.Value("PackageIdentifier", static s => new PackageIdentifier(s)),
            PackageVersion = reader.Value("PackageVersion", static s => new PackageVersion(s)),
            Channel = reader.String("Channel"),
            Installers = reader.MappingList("Installers", ReadInstallerEntry),
        };
        ReadInstallerFields(reader, manifest);
        ReadManifestFooter(reader, type => manifest.ManifestType = type, version => manifest.ManifestVersion = version);
        return manifest;
    }

    public static DefaultLocaleManifest ReadDefaultLocale(string yaml)
    {
        MappingReader reader = LoadRoot(yaml);
        var manifest = new DefaultLocaleManifest
        {
            Moniker = reader.String("Moniker"),
        };
        ReadLocaleFields(reader, manifest);
        return manifest;
    }

    public static LocaleManifest ReadLocale(string yaml)
    {
        MappingReader reader = LoadRoot(yaml);
        var manifest = new LocaleManifest();
        ReadLocaleFields(reader, manifest);
        return manifest;
    }

    public static VersionManifest ReadVersion(string yaml)
    {
        MappingReader reader = LoadRoot(yaml);
        var manifest = new VersionManifest
        {
            PackageIdentifier = reader.Value("PackageIdentifier", static s => new PackageIdentifier(s)),
            PackageVersion = reader.Value("PackageVersion", static s => new PackageVersion(s)),
            DefaultLocale = reader.Value("DefaultLocale", static s => new LanguageTag(s)),
        };
        ReadManifestFooter(reader, type => manifest.ManifestType = type, version => manifest.ManifestVersion = version);
        return manifest;
    }

    private static Installer ReadInstallerEntry(MappingReader reader)
    {
        var installer = new Installer
        {
            Architecture = reader.Enum("Architecture", YamlValues.ParseArchitecture),
            InstallerUrl = reader.String("InstallerUrl"),
            InstallerSha256 = reader.Value("InstallerSha256", static s => new Sha256Hash(s)),
            SignatureSha256 = reader.Value("SignatureSha256", static s => new Sha256Hash(s)),
        };
        ReadInstallerFields(reader, installer);
        return installer;
    }

    private static void ReadInstallerFields(MappingReader reader, InstallerFieldsBase fields)
    {
        fields.InstallerLocale = reader.Value("InstallerLocale", static s => new LanguageTag(s));
        fields.Platform = reader.EnumList("Platform", YamlValues.ParsePlatform);
        fields.MinimumOSVersion = reader.Value("MinimumOSVersion", static s => new MinimumOSVersion(s));
        fields.InstallerType = reader.Enum("InstallerType", YamlValues.ParseInstallerType);
        fields.NestedInstallerType = reader.Enum("NestedInstallerType", YamlValues.ParseInstallerType);
        fields.NestedInstallerFiles = reader.MappingList("NestedInstallerFiles", static r => new NestedInstallerFile
        {
            RelativeFilePath = r.String("RelativeFilePath"),
            PortableCommandAlias = r.String("PortableCommandAlias"),
        });
        fields.Scope = reader.Enum("Scope", YamlValues.ParseScope);
        fields.InstallModes = reader.EnumList("InstallModes", YamlValues.ParseInstallMode);
        fields.InstallerSwitches = reader.Mapping("InstallerSwitches") is { } switches
            ? new InstallerSwitches
            {
                Silent = switches.String("Silent"),
                SilentWithProgress = switches.String("SilentWithProgress"),
                Interactive = switches.String("Interactive"),
                InstallLocation = switches.String("InstallLocation"),
                Log = switches.String("Log"),
                Upgrade = switches.String("Upgrade"),
                Custom = switches.String("Custom"),
                Repair = switches.String("Repair"),
            }
            : null;
        fields.InstallerSuccessCodes = reader.Int64List("InstallerSuccessCodes");
        fields.ExpectedReturnCodes = reader.MappingList("ExpectedReturnCodes", static r => new ExpectedReturnCode
        {
            InstallerReturnCode = r.Int64("InstallerReturnCode"),
            ReturnResponse = r.Enum("ReturnResponse", YamlValues.ParseReturnResponse),
            ReturnResponseUrl = r.String("ReturnResponseUrl"),
        });
        fields.UpgradeBehavior = reader.Enum("UpgradeBehavior", YamlValues.ParseUpgradeBehavior);
        fields.Commands = reader.StringList("Commands");
        fields.Protocols = reader.StringList("Protocols");
        fields.FileExtensions = reader.StringList("FileExtensions");
        fields.Dependencies = reader.Mapping("Dependencies") is { } dependencies
            ? new Dependencies
            {
                WindowsFeatures = dependencies.StringList("WindowsFeatures"),
                WindowsLibraries = dependencies.StringList("WindowsLibraries"),
                PackageDependencies = dependencies.MappingList("PackageDependencies", static r => new PackageDependency
                {
                    PackageIdentifier = r.Value("PackageIdentifier", static s => new PackageIdentifier(s)),
                    MinimumVersion = r.Value("MinimumVersion", static s => new PackageVersion(s)),
                }),
                ExternalDependencies = dependencies.StringList("ExternalDependencies"),
            }
            : null;
        fields.PackageFamilyName = reader.String("PackageFamilyName");
        fields.ProductCode = reader.String("ProductCode");
        fields.Capabilities = reader.StringList("Capabilities");
        fields.RestrictedCapabilities = reader.StringList("RestrictedCapabilities");
        fields.Markets = reader.Mapping("Markets") is { } markets
            ? new Markets
            {
                AllowedMarkets = markets.StringList("AllowedMarkets"),
                ExcludedMarkets = markets.StringList("ExcludedMarkets"),
            }
            : null;
        fields.InstallerAbortsTerminal = reader.Boolean("InstallerAbortsTerminal");
        fields.ReleaseDate = reader.Date("ReleaseDate");
        fields.InstallLocationRequired = reader.Boolean("InstallLocationRequired");
        fields.RequireExplicitUpgrade = reader.Boolean("RequireExplicitUpgrade");
        fields.DisplayInstallWarnings = reader.Boolean("DisplayInstallWarnings");
        fields.UnsupportedOSArchitectures = reader.EnumList("UnsupportedOSArchitectures", YamlValues.ParseArchitecture);
        fields.UnsupportedArguments = reader.EnumList("UnsupportedArguments", YamlValues.ParseUnsupportedArgument);
        fields.AppsAndFeaturesEntries = reader.MappingList("AppsAndFeaturesEntries", static r => new AppsAndFeaturesEntry
        {
            DisplayName = r.String("DisplayName"),
            Publisher = r.String("Publisher"),
            DisplayVersion = r.String("DisplayVersion"),
            ProductCode = r.String("ProductCode"),
            UpgradeCode = r.String("UpgradeCode"),
            InstallerType = r.Enum("InstallerType", YamlValues.ParseInstallerType),
        });
        fields.ElevationRequirement = reader.Enum("ElevationRequirement", YamlValues.ParseElevationRequirement);
        fields.InstallationMetadata = reader.Mapping("InstallationMetadata") is { } metadata
            ? new InstallationMetadata
            {
                DefaultInstallLocation = metadata.String("DefaultInstallLocation"),
                Files = metadata.MappingList("Files", static r => new InstalledFile
                {
                    RelativeFilePath = r.String("RelativeFilePath"),
                    FileSha256 = r.Value("FileSha256", static s => new Sha256Hash(s)),
                    FileType = r.Enum("FileType", YamlValues.ParseInstalledFileType),
                    InvocationParameter = r.String("InvocationParameter"),
                    DisplayName = r.String("DisplayName"),
                }),
            }
            : null;
        fields.DownloadCommandProhibited = reader.Boolean("DownloadCommandProhibited");
        fields.RepairBehavior = reader.Enum("RepairBehavior", YamlValues.ParseRepairBehavior);
        fields.ArchiveBinariesDependOnPath = reader.Boolean("ArchiveBinariesDependOnPath");
        fields.Authentication = reader.Mapping("Authentication") is { } authentication
            ? new Authentication
            {
                AuthenticationType = authentication.Enum("AuthenticationType", YamlValues.ParseAuthenticationType),
                MicrosoftEntraIdAuthenticationInfo = authentication.Mapping("MicrosoftEntraIdAuthenticationInfo") is { } info
                    ? new MicrosoftEntraIdAuthenticationInfo
                    {
                        Resource = info.String("Resource"),
                        Scope = info.String("Scope"),
                    }
                    : null,
            }
            : null;
    }

    private static void ReadLocaleFields(MappingReader reader, LocaleManifest manifest)
    {
        manifest.PackageIdentifier = reader.Value("PackageIdentifier", static s => new PackageIdentifier(s));
        manifest.PackageVersion = reader.Value("PackageVersion", static s => new PackageVersion(s));
        manifest.PackageLocale = reader.Value("PackageLocale", static s => new LanguageTag(s));
        manifest.Publisher = reader.String("Publisher");
        manifest.PublisherUrl = reader.String("PublisherUrl");
        manifest.PublisherSupportUrl = reader.String("PublisherSupportUrl");
        manifest.PrivacyUrl = reader.String("PrivacyUrl");
        manifest.Author = reader.String("Author");
        manifest.PackageName = reader.String("PackageName");
        manifest.PackageUrl = reader.String("PackageUrl");
        manifest.License = reader.String("License");
        manifest.LicenseUrl = reader.String("LicenseUrl");
        manifest.Copyright = reader.String("Copyright");
        manifest.CopyrightUrl = reader.String("CopyrightUrl");
        manifest.ShortDescription = reader.String("ShortDescription");
        manifest.Description = reader.String("Description");
        manifest.Tags = reader.StringList("Tags");
        manifest.Agreements = reader.MappingList("Agreements", static r => new PackageAgreement
        {
            AgreementLabel = r.String("AgreementLabel"),
            Agreement = r.String("Agreement"),
            AgreementUrl = r.String("AgreementUrl"),
        });
        manifest.ReleaseNotes = reader.String("ReleaseNotes");
        manifest.ReleaseNotesUrl = reader.String("ReleaseNotesUrl");
        manifest.PurchaseUrl = reader.String("PurchaseUrl");
        manifest.InstallationNotes = reader.String("InstallationNotes");
        manifest.Documentations = reader.MappingList("Documentations", static r => new Documentation
        {
            DocumentLabel = r.String("DocumentLabel"),
            DocumentUrl = r.String("DocumentUrl"),
        });
        manifest.Icons = reader.MappingList("Icons", static r => new Icon
        {
            IconUrl = r.String("IconUrl"),
            IconFileType = r.Enum("IconFileType", YamlValues.ParseIconFileType),
            IconResolution = r.Enum("IconResolution", YamlValues.ParseIconResolution),
            IconTheme = r.Enum("IconTheme", YamlValues.ParseIconTheme),
            IconSha256 = r.Value("IconSha256", static s => new Sha256Hash(s)),
        });
        ReadManifestFooter(reader, type => manifest.ManifestType = type, version => manifest.ManifestVersion = version);
    }

    private static void ReadManifestFooter(MappingReader reader, Action<ManifestType> setType, Action<ManifestVersion> setVersion)
    {
        if (reader.Enum("ManifestType", YamlValues.ParseManifestType) is { } type)
        {
            setType(type);
        }

        if (reader.Value("ManifestVersion", static s => new ManifestVersion(s)) is { } version)
        {
            setVersion(version);
        }
    }

    private static MappingReader LoadRoot(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        using var stringReader = new StringReader(yaml);
        stream.Load(stringReader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("The document is not a YAML mapping and cannot be a WinGet manifest.");
        }

        return new MappingReader(root);
    }

    /// <summary>Typed access to the children of a YAML mapping node.</summary>
    private sealed class MappingReader
    {
        private readonly Dictionary<string, YamlNode> _children;

        public MappingReader(YamlMappingNode node)
        {
            _children = new Dictionary<string, YamlNode>(node.Children.Count, StringComparer.Ordinal);
            foreach ((YamlNode key, YamlNode value) in node.Children)
            {
                if (key is YamlScalarNode { Value: { } name })
                {
                    _children[name] = value;
                }
            }
        }

        public string? String(string key) => ScalarValue(key);

        public T? Value<T>(string key, Func<string, T> parse)
            where T : class
            => ScalarValue(key) is { } value ? parse(value) : null;

        public T? Enum<T>(string key, Func<string, T> parse)
            where T : struct
            => ScalarValue(key) is { } value ? parse(value) : null;

        public long? Int64(string key)
            => ScalarValue(key) is { } value
                ? long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                : null;

        public bool? Boolean(string key) => ScalarValue(key) switch
        {
            null => null,
            { } value when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            { } value when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            { } value => throw new FormatException($"'{value}' is not a valid boolean value."),
        };

        public DateOnly? Date(string key)
        {
            string? value = ScalarValue(key);
            if (value is null)
            {
                return null;
            }

            // Plain dates per the schema, but tolerate full timestamps seen in the wild.
            string datePart = value.Length > 10 ? value[..10] : value;
            return DateOnly.ParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public List<string>? StringList(string key)
            => SequenceItems(key)?.ConvertAll(static node => RequireScalar(node));

        public List<long>? Int64List(string key)
            => SequenceItems(key)?.ConvertAll(static node =>
                long.Parse(RequireScalar(node), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));

        public List<T>? EnumList<T>(string key, Func<string, T> parse)
            where T : struct
            => SequenceItems(key)?.ConvertAll(node => parse(RequireScalar(node)));

        public List<T>? MappingList<T>(string key, Func<MappingReader, T> map)
        {
            List<YamlNode>? items = SequenceItems(key);
            return items?.ConvertAll(node => node is YamlMappingNode mapping
                ? map(new MappingReader(mapping))
                : throw new FormatException($"Each item of '{key}' must be a YAML mapping."));
        }

        public MappingReader? Mapping(string key)
        {
            if (!_children.TryGetValue(key, out YamlNode? node))
            {
                return null;
            }

            return node switch
            {
                YamlMappingNode mapping => new MappingReader(mapping),
                YamlScalarNode scalar when IsNullScalar(scalar) => null,
                _ => throw new FormatException($"'{key}' must be a YAML mapping."),
            };
        }

        private string? ScalarValue(string key)
        {
            if (!_children.TryGetValue(key, out YamlNode? node))
            {
                return null;
            }

            if (node is not YamlScalarNode scalar)
            {
                throw new FormatException($"'{key}' must be a scalar value.");
            }

            return IsNullScalar(scalar) ? null : scalar.Value;
        }

        private List<YamlNode>? SequenceItems(string key)
        {
            if (!_children.TryGetValue(key, out YamlNode? node))
            {
                return null;
            }

            return node switch
            {
                YamlSequenceNode sequence => [.. sequence.Children],
                YamlScalarNode scalar when IsNullScalar(scalar) => null,
                _ => throw new FormatException($"'{key}' must be a YAML sequence."),
            };
        }

        private static string RequireScalar(YamlNode node)
            => node is YamlScalarNode { Value: { } value }
                ? value
                : throw new FormatException("Expected a scalar YAML value.");

        private static bool IsNullScalar(YamlScalarNode scalar)
            => scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";
    }
}
