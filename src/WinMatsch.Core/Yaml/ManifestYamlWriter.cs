namespace WinMatsch.Core.Yaml;

/// <summary>
/// Serializes manifest models to YAML with the canonical field order used across winget-pkgs
/// (schema property order), stable byte-for-byte output, and the standard comment headers.
/// Structurally required fields (identifier, version, installer URL/hash/architecture, ...)
/// are enforced here; everything else is the domain of validation rules.
/// </summary>
public static class ManifestYamlWriter
{
    public static string Serialize(InstallerManifest manifest, ManifestWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var emitter = new YamlEmitter();
        WriteHeader(emitter, manifest.ManifestType, manifest.ManifestVersion, options);

        emitter.Scalar("PackageIdentifier", Require(manifest.PackageIdentifier, "PackageIdentifier").Value);
        emitter.Scalar("PackageVersion", Require(manifest.PackageVersion, "PackageVersion").Value);
        emitter.Scalar("Channel", manifest.Channel);
        emitter.Scalar("InstallerLocale", manifest.InstallerLocale?.Value);
        emitter.ScalarSequence("Platform", manifest.Platform, static p => p.ToYaml());
        emitter.Scalar("MinimumOSVersion", manifest.MinimumOSVersion?.Value);
        emitter.Scalar("InstallerType", manifest.InstallerType?.ToYaml());
        emitter.Scalar("NestedInstallerType", manifest.NestedInstallerType?.ToYaml());
        WriteNestedInstallerFiles(emitter, manifest.NestedInstallerFiles);
        emitter.Scalar("Scope", manifest.Scope?.ToYaml());
        WriteInstallerFieldsTail(emitter, manifest);

        if (manifest.Installers is not { Count: > 0 })
        {
            throw MissingRequiredField("Installers");
        }

        emitter.MappingSequence("Installers", manifest.Installers, static (e, installer) => WriteInstaller(e, installer));

        emitter.Scalar("ManifestType", manifest.ManifestType.ToYaml());
        emitter.Scalar("ManifestVersion", manifest.ManifestVersion.Value);
        return emitter.ToString();
    }

    public static string Serialize(VersionManifest manifest, ManifestWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var emitter = new YamlEmitter();
        WriteHeader(emitter, manifest.ManifestType, manifest.ManifestVersion, options);

        emitter.Scalar("PackageIdentifier", Require(manifest.PackageIdentifier, "PackageIdentifier").Value);
        emitter.Scalar("PackageVersion", Require(manifest.PackageVersion, "PackageVersion").Value);
        emitter.Scalar("DefaultLocale", Require(manifest.DefaultLocale, "DefaultLocale").Value);
        emitter.Scalar("ManifestType", manifest.ManifestType.ToYaml());
        emitter.Scalar("ManifestVersion", manifest.ManifestVersion.Value);
        return emitter.ToString();
    }

    public static string Serialize(LocaleManifest manifest, ManifestWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var emitter = new YamlEmitter();
        WriteHeader(emitter, manifest.ManifestType, manifest.ManifestVersion, options);

        emitter.Scalar("PackageIdentifier", Require(manifest.PackageIdentifier, "PackageIdentifier").Value);
        emitter.Scalar("PackageVersion", Require(manifest.PackageVersion, "PackageVersion").Value);
        emitter.Scalar("PackageLocale", Require(manifest.PackageLocale, "PackageLocale").Value);
        emitter.Scalar("Publisher", manifest.Publisher);
        emitter.Scalar("PublisherUrl", manifest.PublisherUrl);
        emitter.Scalar("PublisherSupportUrl", manifest.PublisherSupportUrl);
        emitter.Scalar("PrivacyUrl", manifest.PrivacyUrl);
        emitter.Scalar("Author", manifest.Author);
        emitter.Scalar("PackageName", manifest.PackageName);
        emitter.Scalar("PackageUrl", manifest.PackageUrl);
        emitter.Scalar("License", manifest.License);
        emitter.Scalar("LicenseUrl", manifest.LicenseUrl);
        emitter.Scalar("Copyright", manifest.Copyright);
        emitter.Scalar("CopyrightUrl", manifest.CopyrightUrl);
        emitter.Scalar("ShortDescription", manifest.ShortDescription);
        emitter.Scalar("Description", manifest.Description);
        if (manifest is DefaultLocaleManifest defaultLocale)
        {
            emitter.Scalar("Moniker", defaultLocale.Moniker);
        }

        emitter.StringSequence("Tags", manifest.Tags);
        emitter.MappingSequence("Agreements", manifest.Agreements, static (e, agreement) =>
        {
            e.Scalar("AgreementLabel", agreement.AgreementLabel);
            e.Scalar("Agreement", agreement.Agreement);
            e.Scalar("AgreementUrl", agreement.AgreementUrl);
        });
        emitter.Scalar("ReleaseNotes", manifest.ReleaseNotes);
        emitter.Scalar("ReleaseNotesUrl", manifest.ReleaseNotesUrl);
        emitter.Scalar("PurchaseUrl", manifest.PurchaseUrl);
        emitter.Scalar("InstallationNotes", manifest.InstallationNotes);
        emitter.MappingSequence("Documentations", manifest.Documentations, static (e, documentation) =>
        {
            e.Scalar("DocumentLabel", documentation.DocumentLabel);
            e.Scalar("DocumentUrl", documentation.DocumentUrl);
        });
        emitter.MappingSequence("Icons", manifest.Icons, static (e, icon) =>
        {
            e.Scalar("IconUrl", icon.IconUrl);
            e.Scalar("IconFileType", icon.IconFileType?.ToYaml());
            e.Scalar("IconResolution", icon.IconResolution?.ToYaml());
            e.Scalar("IconTheme", icon.IconTheme?.ToYaml());
            e.Scalar("IconSha256", icon.IconSha256?.Value);
        });
        emitter.Scalar("ManifestType", manifest.ManifestType.ToYaml());
        emitter.Scalar("ManifestVersion", manifest.ManifestVersion.Value);
        return emitter.ToString();
    }

    private static void WriteInstaller(YamlEmitter emitter, Installer installer)
    {
        emitter.Scalar("InstallerLocale", installer.InstallerLocale?.Value);
        emitter.ScalarSequence("Platform", installer.Platform, static p => p.ToYaml());
        emitter.Scalar("MinimumOSVersion", installer.MinimumOSVersion?.Value);
        emitter.Scalar("Architecture", Require(installer.Architecture, "Architecture").ToYaml());
        emitter.Scalar("InstallerType", installer.InstallerType?.ToYaml());
        emitter.Scalar("NestedInstallerType", installer.NestedInstallerType?.ToYaml());
        WriteNestedInstallerFiles(emitter, installer.NestedInstallerFiles);
        emitter.Scalar("Scope", installer.Scope?.ToYaml());
        emitter.Scalar("InstallerUrl", Require(installer.InstallerUrl, "InstallerUrl"));
        emitter.Scalar("InstallerSha256", Require(installer.InstallerSha256, "InstallerSha256").Value);
        emitter.Scalar("SignatureSha256", installer.SignatureSha256?.Value);
        WriteInstallerFieldsTail(emitter, installer);
    }

    /// <summary>
    /// The installer fields shared verbatim between the manifest root and installer entries,
    /// from <c>InstallModes</c> onward (the earlier shared fields interleave with entry-specific
    /// fields like <c>Architecture</c> and <c>InstallerUrl</c> and are written by the callers).
    /// </summary>
    private static void WriteInstallerFieldsTail(YamlEmitter emitter, InstallerFieldsBase fields)
    {
        emitter.ScalarSequence("InstallModes", fields.InstallModes, static m => m.ToYaml());
        if (fields.InstallerSwitches is { IsEmpty: false } switches)
        {
            emitter.Mapping("InstallerSwitches", e =>
            {
                e.Scalar("Silent", switches.Silent);
                e.Scalar("SilentWithProgress", switches.SilentWithProgress);
                e.Scalar("Interactive", switches.Interactive);
                e.Scalar("InstallLocation", switches.InstallLocation);
                e.Scalar("Log", switches.Log);
                e.Scalar("Upgrade", switches.Upgrade);
                e.Scalar("Custom", switches.Custom);
                e.Scalar("Repair", switches.Repair);
            });
        }

        emitter.NumberSequence("InstallerSuccessCodes", fields.InstallerSuccessCodes);
        emitter.MappingSequence("ExpectedReturnCodes", fields.ExpectedReturnCodes, static (e, code) =>
        {
            e.Scalar("InstallerReturnCode", code.InstallerReturnCode);
            e.Scalar("ReturnResponse", code.ReturnResponse?.ToYaml());
            e.Scalar("ReturnResponseUrl", code.ReturnResponseUrl);
        });
        emitter.Scalar("UpgradeBehavior", fields.UpgradeBehavior?.ToYaml());
        emitter.StringSequence("Commands", fields.Commands);
        emitter.StringSequence("Protocols", fields.Protocols);
        emitter.StringSequence("FileExtensions", fields.FileExtensions);

        if (fields.Dependencies is { } dependencies
            && (dependencies.WindowsFeatures is { Count: > 0 }
                || dependencies.WindowsLibraries is { Count: > 0 }
                || dependencies.PackageDependencies is { Count: > 0 }
                || dependencies.ExternalDependencies is { Count: > 0 }))
        {
            emitter.Mapping("Dependencies", e =>
            {
                e.StringSequence("WindowsFeatures", dependencies.WindowsFeatures);
                e.StringSequence("WindowsLibraries", dependencies.WindowsLibraries);
                e.MappingSequence("PackageDependencies", dependencies.PackageDependencies, static (pe, dependency) =>
                {
                    pe.Scalar("PackageIdentifier", Require(dependency.PackageIdentifier, "PackageDependencies.PackageIdentifier").Value);
                    pe.Scalar("MinimumVersion", dependency.MinimumVersion?.Value);
                });
                e.StringSequence("ExternalDependencies", dependencies.ExternalDependencies);
            });
        }

        emitter.Scalar("PackageFamilyName", fields.PackageFamilyName);
        emitter.Scalar("ProductCode", fields.ProductCode);
        emitter.StringSequence("Capabilities", fields.Capabilities);
        emitter.StringSequence("RestrictedCapabilities", fields.RestrictedCapabilities);

        if (fields.Markets is { } markets
            && (markets.AllowedMarkets is { Count: > 0 } || markets.ExcludedMarkets is { Count: > 0 }))
        {
            emitter.Mapping("Markets", e =>
            {
                e.StringSequence("AllowedMarkets", markets.AllowedMarkets);
                e.StringSequence("ExcludedMarkets", markets.ExcludedMarkets);
            });
        }

        emitter.Scalar("InstallerAbortsTerminal", fields.InstallerAbortsTerminal);
        emitter.Scalar("ReleaseDate", fields.ReleaseDate);
        emitter.Scalar("InstallLocationRequired", fields.InstallLocationRequired);
        emitter.Scalar("RequireExplicitUpgrade", fields.RequireExplicitUpgrade);
        emitter.Scalar("DisplayInstallWarnings", fields.DisplayInstallWarnings);
        emitter.ScalarSequence("UnsupportedOSArchitectures", fields.UnsupportedOSArchitectures, static a => a.ToYaml());
        emitter.ScalarSequence("UnsupportedArguments", fields.UnsupportedArguments, static a => a.ToYaml());
        emitter.MappingSequence("AppsAndFeaturesEntries", fields.AppsAndFeaturesEntries, static (e, entry) =>
        {
            e.Scalar("DisplayName", entry.DisplayName);
            e.Scalar("Publisher", entry.Publisher);
            e.Scalar("DisplayVersion", entry.DisplayVersion);
            e.Scalar("ProductCode", entry.ProductCode);
            e.Scalar("UpgradeCode", entry.UpgradeCode);
            e.Scalar("InstallerType", entry.InstallerType?.ToYaml());
        });
        emitter.Scalar("ElevationRequirement", fields.ElevationRequirement?.ToYaml());

        if (fields.InstallationMetadata is { } metadata
            && (metadata.DefaultInstallLocation is not null || metadata.Files is { Count: > 0 }))
        {
            emitter.Mapping("InstallationMetadata", e =>
            {
                e.Scalar("DefaultInstallLocation", metadata.DefaultInstallLocation);
                e.MappingSequence("Files", metadata.Files, static (fe, file) =>
                {
                    fe.Scalar("RelativeFilePath", file.RelativeFilePath);
                    fe.Scalar("FileSha256", file.FileSha256?.Value);
                    fe.Scalar("FileType", file.FileType?.ToYaml());
                    fe.Scalar("InvocationParameter", file.InvocationParameter);
                    fe.Scalar("DisplayName", file.DisplayName);
                });
            });
        }

        emitter.Scalar("DownloadCommandProhibited", fields.DownloadCommandProhibited);
        emitter.Scalar("RepairBehavior", fields.RepairBehavior?.ToYaml());
        emitter.Scalar("ArchiveBinariesDependOnPath", fields.ArchiveBinariesDependOnPath);

        if (fields.Authentication is { } authentication && authentication.AuthenticationType is not null)
        {
            emitter.Mapping("Authentication", e =>
            {
                e.Scalar("AuthenticationType", authentication.AuthenticationType?.ToYaml());
                if (authentication.MicrosoftEntraIdAuthenticationInfo is { } info
                    && (info.Resource is not null || info.Scope is not null))
                {
                    e.Mapping("MicrosoftEntraIdAuthenticationInfo", ie =>
                    {
                        ie.Scalar("Resource", info.Resource);
                        ie.Scalar("Scope", info.Scope);
                    });
                }
            });
        }
    }

    private static void WriteNestedInstallerFiles(YamlEmitter emitter, IReadOnlyList<NestedInstallerFile>? files)
    {
        emitter.MappingSequence("NestedInstallerFiles", files, static (e, file) =>
        {
            e.Scalar("RelativeFilePath", file.RelativeFilePath);
            e.Scalar("PortableCommandAlias", file.PortableCommandAlias);
        });
    }

    private static void WriteHeader(YamlEmitter emitter, ManifestType type, ManifestVersion version, ManifestWriteOptions? options)
    {
        options ??= ManifestWriteOptions.Default;

        bool wroteAnyHeader = false;
        if (options.CreatedWith is { } createdWith)
        {
            emitter.Comment($"Created with {createdWith}");
            wroteAnyHeader = true;
        }

        if (options.IncludeSchemaHeader)
        {
            emitter.Comment($"yaml-language-server: $schema=https://aka.ms/winget-manifest.{type.ToYaml()}.{version.Value}.schema.json");
            wroteAnyHeader = true;
        }

        if (wroteAnyHeader)
        {
            emitter.BlankLine();
        }
    }

    private static T Require<T>(T? value, string fieldName)
        where T : class
        => value ?? throw MissingRequiredField(fieldName);

    private static T Require<T>(T? value, string fieldName)
        where T : struct
        => value ?? throw MissingRequiredField(fieldName);

    private static InvalidOperationException MissingRequiredField(string fieldName)
        => new($"Cannot serialize the manifest: required field '{fieldName}' is not set.");
}
