using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Rules;

/// <summary>Applies field, URL, scope-layout, and approved learned package overrides last.</summary>
public sealed class ApplyOverridePackFieldsRule(OverridePackSet? overridePacks = null) : IRule
{
    private readonly OverridePackSet _overridePacks = overridePacks ?? OverridePackSet.Empty;

    public string Id => RuleIds.ApplyOverridePackFields;

    public RuleCategory Category => RuleCategory.Quirk;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Applies validated package override fields before final validation and emission.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? pack)
            || pack is null)
        {
            return;
        }

        ApplyScopeLayout(context, pack.ScopeLayout);
        if (context.Previous is { } previous)
        {
            foreach (string selector in pack.PreservedFields.Order(StringComparer.Ordinal))
            {
                PreserveSelector(previous, context.Manifests, selector);
                context.AddTrace(this, $"{selector}: evaluated explicit preservedFields selector.");
            }

            ApplyLearnedFields(context, previous, pack);
        }

        int replacedUrls = ApplyMetadataUrlReplacements(
            context.Manifests,
            pack.MetadataUrlReplacements);
        if (pack.MetadataUrlReplacements.Count > 0)
        {
            context.AddTrace(
                this,
                $"metadataUrlReplacements: applied {replacedUrls} exact metadata URL replacement(s).");
        }

        foreach (string selector in pack.DroppedFields.Order(StringComparer.Ordinal))
        {
            DropSelector(context.Manifests, selector);
            context.AddTrace(this, $"{selector}: applied explicit droppedFields selector.");
        }
    }

    private void ApplyScopeLayout(ManifestContext context, ScopeLayoutOverride? requested)
    {
        if (requested is null)
        {
            return;
        }

        context.AddTrace(this, $"scopeLayout: applying {requested.Value} layout.");

        ScopeLayoutOverride layout = requested.Value;
        if (layout == ScopeLayoutOverride.Preserve)
        {
            if (context.Previous is null)
            {
                context.AddFinding(
                    this,
                    RuleSeverity.Error,
                    "scopeLayout Preserve requires a previous merged manifest.",
                    "Installer.Scope");
                return;
            }

            layout = context.Previous.Installer.Scope is null
                ? ScopeLayoutOverride.PerInstaller
                : ScopeLayoutOverride.Root;
        }

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer> installers = manifest.Installers ?? [];
        if (layout == ScopeLayoutOverride.PerInstaller)
        {
            foreach (Installer installer in installers)
            {
                installer.Scope ??= manifest.Scope;
            }

            manifest.Scope = null;
            return;
        }

        Scope?[] scopes = installers
            .Select(installer => installer.Scope ?? manifest.Scope)
            .Distinct()
            .ToArray();
        if (scopes.Length != 1 || scopes[0] is null)
        {
            context.AddFinding(
                this,
                RuleSeverity.Error,
                "scopeLayout Root requires every installer to have the same explicit effective scope.",
                "Installer.Scope");
            return;
        }

        manifest.Scope = scopes[0];
        foreach (Installer installer in installers)
        {
            installer.Scope = null;
        }
    }

    private void ApplyLearnedFields(
        ManifestContext context,
        PackageManifests previous,
        OverridePack pack)
    {
        if (pack.LearnedFields.IsDefaultOrEmpty
            || !ManifestSnapshot.TryCapture(previous, out ManifestSnapshot before)
            || !ManifestSnapshot.TryCapture(context.Manifests, out ManifestSnapshot after))
        {
            return;
        }

        RawManifestChange[] changes =
        [
            .. before.Diff(after).Where(static change => !change.IsPairing),
        ];
        foreach (LearnedFieldOverride learned in pack.LearnedFields)
        {
            if (IsExplicitlyDropped(pack.DroppedFields, learned))
            {
                context.AddTrace(this, $"{learned.SemanticPath}: explicit droppedFields entry wins over learned preservation.");
                continue;
            }

            if (learned.DocumentKey == "installer"
                && learned.InstallerSelectorSha256 is not null)
            {
                ApplyLearnedInstallerField(context, previous, learned);
                continue;
            }

            RawManifestChange[] matches = changes
                .Where(change => string.Equals(change.DocumentKey, learned.DocumentKey, StringComparison.Ordinal)
                    && string.Equals(change.SemanticPath, learned.SemanticPath, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0)
            {
                context.AddTrace(
                    this,
                    $"{learned.SemanticPath}: approved learned value already holds or no reviewed identity is present.");
                continue;
            }

            if (matches.Length != 1
                || !string.Equals(Hash(matches[0].Before), learned.ValueSha256, StringComparison.Ordinal)
                || !string.Equals(matches[0].Before, learned.Value, StringComparison.Ordinal)
                || !TrySetLearnedValue(context.Manifests, learned.DocumentKey, matches[0].FieldPath, learned.Value))
            {
                context.AddFinding(
                    this,
                    RuleSeverity.Warning,
                    $"Approved learned override '{learned.SemanticPath}' no longer matches its reviewed correction identity; review it again.",
                    matches.FirstOrDefault()?.FieldPath ?? learned.SemanticPath);
            }
        }
    }

    private void ApplyLearnedInstallerField(
        ManifestContext context,
        PackageManifests previous,
        LearnedFieldOverride learned)
    {
        string field = learned.SemanticPath.Split('.').Last();
        if (learned.InstallerSelectorSha256 is null
            || previous.Installer.Installers is not { } previousInstallers
            || context.Manifests.Installer.Installers is not { } currentInstallers)
        {
            AddStaleLearnedFinding(context, learned, learned.SemanticPath, "selector metadata is unavailable");
            return;
        }

        int[] matchingPrevious =
        [
            .. Enumerable.Range(0, previousInstallers.Count).Where(index =>
                    string.Equals(
                        LearnedInstallerSelector.Create(previous, index, field),
                        learned.InstallerSelectorSha256,
                        StringComparison.Ordinal)),
            ];
        if (matchingPrevious.Length != 1)
        {
            AddStaleLearnedFinding(
                context,
                learned,
                learned.SemanticPath,
                $"matched {matchingPrevious.Length} previous installers");
            return;
        }

        int previousIndex = matchingPrevious[0];
        int?[] previousByCurrent = ManifestSnapshot.MatchInstallerIndices(
            previous,
            context.Manifests);
        int[] matchingCurrent =
        [
            .. Enumerable.Range(0, currentInstallers.Count).Where(index =>
                    previousByCurrent[index] == previousIndex
                    && string.Equals(
                        LearnedInstallerSelector.Create(context.Manifests, index, field),
                        learned.InstallerSelectorSha256,
                        StringComparison.Ordinal)),
            ];
        string? previousValue = LearnedInstallerSelector.GetValue(
            previous.Installer,
            previousInstallers[previousIndex],
            field);
        if (matchingCurrent.Length != 1
            || !string.Equals(previousValue, learned.Value, StringComparison.Ordinal)
            || !string.Equals(Hash(previousValue), learned.ValueSha256, StringComparison.Ordinal))
        {
            AddStaleLearnedFinding(
                context,
                learned,
                learned.SemanticPath,
                $"matched {matchingCurrent.Length} current installers or the prior approved value changed");
            return;
        }

        int currentIndex = matchingCurrent[0];
        string? currentValue = LearnedInstallerSelector.GetValue(
            context.Manifests.Installer,
            currentInstallers[currentIndex],
            field);
        if (string.Equals(currentValue, learned.Value, StringComparison.Ordinal))
        {
            context.AddTrace(
                this,
                $"{learned.SemanticPath}: approved learned installer value already holds.");
            return;
        }

        string fieldPath = $"Installers[{currentIndex}].{field}";
        if (!TrySetLearnedValue(
                context.Manifests,
                learned.DocumentKey,
                fieldPath,
                learned.Value))
        {
            AddStaleLearnedFinding(context, learned, fieldPath, "the approved value could not be applied");
        }
    }

    private void AddStaleLearnedFinding(
        ManifestContext context,
        LearnedFieldOverride learned,
        string path,
        string reason)
        => context.AddFinding(
            this,
            RuleSeverity.Warning,
            $"Approved learned override '{learned.SemanticPath}' no longer matches its reviewed installer identity ({reason}); review it again.",
            path);

    private static bool IsExplicitlyDropped(
        IEnumerable<string> droppedFields,
        LearnedFieldOverride learned)
    {
        string field = learned.SemanticPath.Split('.').Last();
        return droppedFields.Any(selector =>
            string.Equals(selector, field, StringComparison.Ordinal)
            || learned.DocumentKey == "defaultLocale"
                && string.Equals(selector, $"DefaultLocale.{field}", StringComparison.Ordinal)
            || learned.DocumentKey == "installer"
                && (string.Equals(selector, $"Installer.{field}", StringComparison.Ordinal)
                    || string.Equals(selector, $"Installers[*].{field}", StringComparison.Ordinal)));
    }

    private static int ApplyMetadataUrlReplacements(
        PackageManifests manifests,
        ImmutableDictionary<string, string> replacements)
    {
        if (replacements.Count == 0)
        {
            return 0;
        }

        int count = ReplaceLocaleUrls(manifests.DefaultLocale, replacements);
        foreach (LocaleManifest locale in manifests.Locales)
        {
            count += ReplaceLocaleUrls(locale, replacements);
        }

        return count;
    }

    private static int ReplaceLocaleUrls(
        LocaleManifest locale,
        ImmutableDictionary<string, string> replacements)
    {
        int count = 0;
        locale.PublisherUrl = Replace(locale.PublisherUrl, replacements, ref count);
        locale.PublisherSupportUrl = Replace(locale.PublisherSupportUrl, replacements, ref count);
        locale.PrivacyUrl = Replace(locale.PrivacyUrl, replacements, ref count);
        locale.PackageUrl = Replace(locale.PackageUrl, replacements, ref count);
        locale.LicenseUrl = Replace(locale.LicenseUrl, replacements, ref count);
        locale.CopyrightUrl = Replace(locale.CopyrightUrl, replacements, ref count);
        locale.ReleaseNotesUrl = Replace(locale.ReleaseNotesUrl, replacements, ref count);
        locale.PurchaseUrl = Replace(locale.PurchaseUrl, replacements, ref count);
        if (locale.Documentations is not null)
        {
            foreach (Documentation documentation in locale.Documentations)
            {
                documentation.DocumentUrl = Replace(
                    documentation.DocumentUrl,
                    replacements,
                    ref count);
            }
        }

        return count;
    }

    private static string? Replace(
        string? value,
        ImmutableDictionary<string, string> replacements,
        ref int count)
    {
        if (value is null || !replacements.TryGetValue(value, out string? replacement))
        {
            return value;
        }

        count++;
        return replacement;
    }

    private static void PreserveSelector(
        PackageManifests previous,
        PackageManifests current,
        string selector)
    {
        if (selector.StartsWith("DefaultLocale.", StringComparison.Ordinal))
        {
            CopyLocaleField(
                previous.DefaultLocale,
                current.DefaultLocale,
                selector["DefaultLocale.".Length..],
                missingOnly: true);
            return;
        }

        if (selector.StartsWith("Locales[*].", StringComparison.Ordinal))
        {
            string field = selector["Locales[*].".Length..];
            foreach (LocaleManifest locale in current.Locales)
            {
                LocaleManifest? source = previous.Locales.SingleOrDefault(candidate =>
                    candidate.PackageLocale == locale.PackageLocale);
                if (source is not null)
                {
                    CopyLocaleField(source, locale, field, missingOnly: true);
                }
            }

            return;
        }

        if (selector.StartsWith("Installer.", StringComparison.Ordinal))
        {
            CopyInstallerField(
                previous.Installer,
                current.Installer,
                selector["Installer.".Length..],
                missingOnly: true);
            return;
        }

        if (selector.StartsWith("Installers[*].", StringComparison.Ordinal))
        {
            string field = selector["Installers[*].".Length..];
            int?[] previousByCurrent = ManifestSnapshot.MatchInstallerIndices(previous, current);
            for (int currentIndex = 0; currentIndex < previousByCurrent.Length; currentIndex++)
            {
                if (previousByCurrent[currentIndex] is int previousIndex)
                {
                    CopyInstallerField(
                        previous.Installer.Installers![previousIndex],
                        current.Installer.Installers![currentIndex],
                        field,
                        missingOnly: true);
                }
            }

            return;
        }

        if (HasLocaleField(selector))
        {
            CopyLocaleField(previous.DefaultLocale, current.DefaultLocale, selector, missingOnly: true);
        }

        if (HasInstallerField(selector))
        {
            CopyInstallerField(previous.Installer, current.Installer, selector, missingOnly: true);
        }
    }

    private static void DropSelector(PackageManifests manifests, string selector)
    {
        if (selector.StartsWith("DefaultLocale.", StringComparison.Ordinal))
        {
            ClearLocaleField(manifests.DefaultLocale, selector["DefaultLocale.".Length..]);
            return;
        }

        if (selector.StartsWith("Locales[*].", StringComparison.Ordinal))
        {
            string field = selector["Locales[*].".Length..];
            foreach (LocaleManifest locale in manifests.Locales)
            {
                ClearLocaleField(locale, field);
            }

            return;
        }

        if (selector.StartsWith("Installer.", StringComparison.Ordinal))
        {
            ClearInstallerField(manifests.Installer, selector["Installer.".Length..]);
            return;
        }

        if (selector.StartsWith("Installers[*].", StringComparison.Ordinal))
        {
            string field = selector["Installers[*].".Length..];
            foreach (Installer installer in manifests.Installer.Installers ?? [])
            {
                ClearInstallerField(installer, field);
            }

            return;
        }

        if (HasLocaleField(selector))
        {
            ClearLocaleField(manifests.DefaultLocale, selector);
            foreach (LocaleManifest locale in manifests.Locales)
            {
                ClearLocaleField(locale, selector);
            }
        }

        if (HasInstallerField(selector))
        {
            ClearInstallerField(manifests.Installer, selector);
            foreach (Installer installer in manifests.Installer.Installers ?? [])
            {
                ClearInstallerField(installer, selector);
            }
        }
    }

    private static bool TrySetLearnedValue(
        PackageManifests manifests,
        string documentKey,
        string fieldPath,
        string value)
    {
        if (documentKey == "defaultLocale")
        {
            return SetLearnedLocaleValue(manifests.DefaultLocale, fieldPath, value);
        }

        if (documentKey != "installer")
        {
            return false;
        }

        InstallerFieldsBase target = manifests.Installer;
        string field = fieldPath;
        if (fieldPath.StartsWith("Installers[", StringComparison.Ordinal))
        {
            int close = fieldPath.IndexOf(']', "Installers[".Length);
            if (close < 0
                || !int.TryParse(fieldPath.AsSpan("Installers[".Length, close - "Installers[".Length), out int index)
                || manifests.Installer.Installers is not { } installers
                || index < 0
                || index >= installers.Count
                || close + 2 > fieldPath.Length)
            {
                return false;
            }

            target = installers[index];
            field = fieldPath[(close + 2)..];
        }

        switch (field)
        {
            case "Architecture" when target is Installer installer
                && Enum.TryParse(value, ignoreCase: true, out Architecture architecture)
                && Enum.IsDefined(architecture):
                installer.Architecture = architecture;
                return true;
            case "InstallerType" when Enum.TryParse(value, ignoreCase: true, out InstallerType installerType)
                && Enum.IsDefined(installerType):
                target.InstallerType = installerType;
                return true;
            case "NestedInstallerType" when Enum.TryParse(value, ignoreCase: true, out InstallerType nestedType)
                && Enum.IsDefined(nestedType):
                target.NestedInstallerType = nestedType;
                return true;
            case "Scope" when Enum.TryParse(value, ignoreCase: true, out Scope scope)
                && Enum.IsDefined(scope):
                target.Scope = scope;
                return true;
            case "InstallerLocale" when LanguageTag.TryCreate(value, out LanguageTag? locale):
                target.InstallerLocale = locale;
                return true;
            default:
                return false;
        }
    }

    private static bool SetLearnedLocaleValue(
        DefaultLocaleManifest locale,
        string field,
        string value)
    {
        switch (field)
        {
            case "Publisher": locale.Publisher = value; return true;
            case "PublisherUrl": locale.PublisherUrl = value; return true;
            case "PublisherSupportUrl": locale.PublisherSupportUrl = value; return true;
            case "PrivacyUrl": locale.PrivacyUrl = value; return true;
            case "Author": locale.Author = value; return true;
            case "PackageName": locale.PackageName = value; return true;
            case "PackageUrl": locale.PackageUrl = value; return true;
            case "License": locale.License = value; return true;
            case "LicenseUrl": locale.LicenseUrl = value; return true;
            case "Copyright": locale.Copyright = value; return true;
            case "CopyrightUrl": locale.CopyrightUrl = value; return true;
            case "ShortDescription": locale.ShortDescription = value; return true;
            case "Description": locale.Description = value; return true;
            case "ReleaseNotes": locale.ReleaseNotes = value; return true;
            case "ReleaseNotesUrl": locale.ReleaseNotesUrl = value; return true;
            case "PurchaseUrl": locale.PurchaseUrl = value; return true;
            case "InstallationNotes": locale.InstallationNotes = value; return true;
            case "Moniker": locale.Moniker = value; return true;
            default: return false;
        }
    }

    private static void CopyLocaleField(
        LocaleManifest source,
        LocaleManifest target,
        string field,
        bool missingOnly)
    {
        if (missingOnly && !LocaleFieldMissing(target, field))
        {
            return;
        }

        switch (field)
        {
            case "Publisher": target.Publisher = source.Publisher; break;
            case "PublisherUrl": target.PublisherUrl = source.PublisherUrl; break;
            case "PublisherSupportUrl": target.PublisherSupportUrl = source.PublisherSupportUrl; break;
            case "PrivacyUrl": target.PrivacyUrl = source.PrivacyUrl; break;
            case "Author": target.Author = source.Author; break;
            case "PackageName": target.PackageName = source.PackageName; break;
            case "PackageUrl": target.PackageUrl = source.PackageUrl; break;
            case "License": target.License = source.License; break;
            case "LicenseUrl": target.LicenseUrl = source.LicenseUrl; break;
            case "Copyright": target.Copyright = source.Copyright; break;
            case "CopyrightUrl": target.CopyrightUrl = source.CopyrightUrl; break;
            case "ShortDescription": target.ShortDescription = source.ShortDescription; break;
            case "Description": target.Description = source.Description; break;
            case "Tags": target.Tags = ManifestValues.CloneStringList(source.Tags); break;
            case "Agreements": target.Agreements = ManifestValues.CloneList(source.Agreements, CloneAgreement); break;
            case "ReleaseNotes": target.ReleaseNotes = source.ReleaseNotes; break;
            case "ReleaseNotesUrl": target.ReleaseNotesUrl = source.ReleaseNotesUrl; break;
            case "PurchaseUrl": target.PurchaseUrl = source.PurchaseUrl; break;
            case "InstallationNotes": target.InstallationNotes = source.InstallationNotes; break;
            case "Documentations": target.Documentations = ManifestValues.CloneList(source.Documentations, ManifestValues.CloneDocumentation); break;
            case "Icons": target.Icons = ManifestValues.CloneList(source.Icons, ManifestValues.CloneIcon); break;
            case "Moniker" when source is DefaultLocaleManifest sourceDefault
                && target is DefaultLocaleManifest targetDefault:
                targetDefault.Moniker = sourceDefault.Moniker;
                break;
        }
    }

    private static bool LocaleFieldMissing(LocaleManifest locale, string field)
        => field switch
        {
            "Publisher" => locale.Publisher is null,
            "PublisherUrl" => locale.PublisherUrl is null,
            "PublisherSupportUrl" => locale.PublisherSupportUrl is null,
            "PrivacyUrl" => locale.PrivacyUrl is null,
            "Author" => locale.Author is null,
            "PackageName" => locale.PackageName is null,
            "PackageUrl" => locale.PackageUrl is null,
            "License" => locale.License is null,
            "LicenseUrl" => locale.LicenseUrl is null,
            "Copyright" => locale.Copyright is null,
            "CopyrightUrl" => locale.CopyrightUrl is null,
            "ShortDescription" => locale.ShortDescription is null,
            "Description" => locale.Description is null,
            "Tags" => locale.Tags is null,
            "Agreements" => locale.Agreements is null,
            "ReleaseNotes" => locale.ReleaseNotes is null,
            "ReleaseNotesUrl" => locale.ReleaseNotesUrl is null,
            "PurchaseUrl" => locale.PurchaseUrl is null,
            "InstallationNotes" => locale.InstallationNotes is null,
            "Documentations" => locale.Documentations is null,
            "Icons" => locale.Icons is null,
            "Moniker" => locale is DefaultLocaleManifest { Moniker: null },
            _ => false,
        };

    private static void ClearLocaleField(LocaleManifest locale, string field)
    {
        switch (field)
        {
            case "Publisher": locale.Publisher = null; break;
            case "PublisherUrl": locale.PublisherUrl = null; break;
            case "PublisherSupportUrl": locale.PublisherSupportUrl = null; break;
            case "PrivacyUrl": locale.PrivacyUrl = null; break;
            case "Author": locale.Author = null; break;
            case "PackageName": locale.PackageName = null; break;
            case "PackageUrl": locale.PackageUrl = null; break;
            case "License": locale.License = null; break;
            case "LicenseUrl": locale.LicenseUrl = null; break;
            case "Copyright": locale.Copyright = null; break;
            case "CopyrightUrl": locale.CopyrightUrl = null; break;
            case "ShortDescription": locale.ShortDescription = null; break;
            case "Description": locale.Description = null; break;
            case "Tags": locale.Tags = null; break;
            case "Agreements": locale.Agreements = null; break;
            case "ReleaseNotes": locale.ReleaseNotes = null; break;
            case "ReleaseNotesUrl": locale.ReleaseNotesUrl = null; break;
            case "PurchaseUrl": locale.PurchaseUrl = null; break;
            case "InstallationNotes": locale.InstallationNotes = null; break;
            case "Documentations": locale.Documentations = null; break;
            case "Icons": locale.Icons = null; break;
            case "Moniker" when locale is DefaultLocaleManifest defaultLocale: defaultLocale.Moniker = null; break;
        }
    }

    private static PackageAgreement CloneAgreement(PackageAgreement source) => new()
    {
        AgreementLabel = source.AgreementLabel,
        Agreement = source.Agreement,
        AgreementUrl = source.AgreementUrl,
    };

    private static void CopyInstallerField(
        InstallerFieldsBase source,
        InstallerFieldsBase target,
        string field,
        bool missingOnly)
    {
        if (missingOnly && !InstallerFieldMissing(target, field))
        {
            return;
        }

        switch (field)
        {
            case "InstallerLocale": target.InstallerLocale = source.InstallerLocale; break;
            case "Platform": target.Platform = source.Platform is null ? null : [.. source.Platform]; break;
            case "MinimumOSVersion": target.MinimumOSVersion = source.MinimumOSVersion; break;
            case "InstallerType": target.InstallerType = source.InstallerType; break;
            case "NestedInstallerType": target.NestedInstallerType = source.NestedInstallerType; break;
            case "NestedInstallerFiles": target.NestedInstallerFiles = ManifestValues.CloneList(source.NestedInstallerFiles, ManifestValues.CloneNestedInstallerFile); break;
            case "Scope": target.Scope = source.Scope; break;
            case "InstallModes": target.InstallModes = source.InstallModes is null ? null : [.. source.InstallModes]; break;
            case "InstallerSwitches": target.InstallerSwitches = source.InstallerSwitches is null ? null : ManifestValues.CloneSwitches(source.InstallerSwitches); break;
            case "InstallerSuccessCodes": target.InstallerSuccessCodes = source.InstallerSuccessCodes is null ? null : [.. source.InstallerSuccessCodes]; break;
            case "ExpectedReturnCodes": target.ExpectedReturnCodes = ManifestValues.CloneList(source.ExpectedReturnCodes, ManifestValues.CloneExpectedReturnCode); break;
            case "UpgradeBehavior": target.UpgradeBehavior = source.UpgradeBehavior; break;
            case "Commands": target.Commands = ManifestValues.CloneStringList(source.Commands); break;
            case "Protocols": target.Protocols = ManifestValues.CloneStringList(source.Protocols); break;
            case "FileExtensions": target.FileExtensions = ManifestValues.CloneStringList(source.FileExtensions); break;
            case "Dependencies": target.Dependencies = source.Dependencies is null ? null : ManifestValues.CloneDependencies(source.Dependencies); break;
            case "PackageFamilyName": target.PackageFamilyName = source.PackageFamilyName; break;
            case "ProductCode": target.ProductCode = source.ProductCode; break;
            case "Capabilities": target.Capabilities = ManifestValues.CloneStringList(source.Capabilities); break;
            case "RestrictedCapabilities": target.RestrictedCapabilities = ManifestValues.CloneStringList(source.RestrictedCapabilities); break;
            case "Markets": target.Markets = source.Markets is null ? null : ManifestValues.CloneMarkets(source.Markets); break;
            case "InstallerAbortsTerminal": target.InstallerAbortsTerminal = source.InstallerAbortsTerminal; break;
            case "ReleaseDate": target.ReleaseDate = source.ReleaseDate; break;
            case "InstallLocationRequired": target.InstallLocationRequired = source.InstallLocationRequired; break;
            case "RequireExplicitUpgrade": target.RequireExplicitUpgrade = source.RequireExplicitUpgrade; break;
            case "DisplayInstallWarnings": target.DisplayInstallWarnings = source.DisplayInstallWarnings; break;
            case "UnsupportedOSArchitectures": target.UnsupportedOSArchitectures = source.UnsupportedOSArchitectures is null ? null : [.. source.UnsupportedOSArchitectures]; break;
            case "UnsupportedArguments": target.UnsupportedArguments = source.UnsupportedArguments is null ? null : [.. source.UnsupportedArguments]; break;
            case "AppsAndFeaturesEntries": target.AppsAndFeaturesEntries = ManifestValues.CloneList(source.AppsAndFeaturesEntries, ManifestValues.CloneAppsAndFeaturesEntry); break;
            case "ElevationRequirement": target.ElevationRequirement = source.ElevationRequirement; break;
            case "InstallationMetadata": target.InstallationMetadata = source.InstallationMetadata is null ? null : ManifestValues.CloneInstallationMetadata(source.InstallationMetadata); break;
            case "DownloadCommandProhibited": target.DownloadCommandProhibited = source.DownloadCommandProhibited; break;
            case "RepairBehavior": target.RepairBehavior = source.RepairBehavior; break;
            case "ArchiveBinariesDependOnPath": target.ArchiveBinariesDependOnPath = source.ArchiveBinariesDependOnPath; break;
        }
    }

    private static bool InstallerFieldMissing(InstallerFieldsBase target, string field)
        => field switch
        {
            "InstallerLocale" => target.InstallerLocale is null,
            "Platform" => target.Platform is null,
            "MinimumOSVersion" => target.MinimumOSVersion is null,
            "InstallerType" => target.InstallerType is null,
            "NestedInstallerType" => target.NestedInstallerType is null,
            "NestedInstallerFiles" => target.NestedInstallerFiles is null,
            "Scope" => target.Scope is null,
            "InstallModes" => target.InstallModes is null,
            "InstallerSwitches" => target.InstallerSwitches is null,
            "InstallerSuccessCodes" => target.InstallerSuccessCodes is null,
            "ExpectedReturnCodes" => target.ExpectedReturnCodes is null,
            "UpgradeBehavior" => target.UpgradeBehavior is null,
            "Commands" => target.Commands is null,
            "Protocols" => target.Protocols is null,
            "FileExtensions" => target.FileExtensions is null,
            "Dependencies" => target.Dependencies is null,
            "PackageFamilyName" => target.PackageFamilyName is null,
            "ProductCode" => target.ProductCode is null,
            "Capabilities" => target.Capabilities is null,
            "RestrictedCapabilities" => target.RestrictedCapabilities is null,
            "Markets" => target.Markets is null,
            "InstallerAbortsTerminal" => target.InstallerAbortsTerminal is null,
            "ReleaseDate" => target.ReleaseDate is null,
            "InstallLocationRequired" => target.InstallLocationRequired is null,
            "RequireExplicitUpgrade" => target.RequireExplicitUpgrade is null,
            "DisplayInstallWarnings" => target.DisplayInstallWarnings is null,
            "UnsupportedOSArchitectures" => target.UnsupportedOSArchitectures is null,
            "UnsupportedArguments" => target.UnsupportedArguments is null,
            "AppsAndFeaturesEntries" => target.AppsAndFeaturesEntries is null,
            "ElevationRequirement" => target.ElevationRequirement is null,
            "InstallationMetadata" => target.InstallationMetadata is null,
            "DownloadCommandProhibited" => target.DownloadCommandProhibited is null,
            "RepairBehavior" => target.RepairBehavior is null,
            "ArchiveBinariesDependOnPath" => target.ArchiveBinariesDependOnPath is null,
            _ => false,
        };

    private static void ClearInstallerField(InstallerFieldsBase target, string field)
    {
        switch (field)
        {
            case "InstallerLocale": target.InstallerLocale = null; break;
            case "Platform": target.Platform = null; break;
            case "MinimumOSVersion": target.MinimumOSVersion = null; break;
            case "InstallerType": target.InstallerType = null; break;
            case "NestedInstallerType": target.NestedInstallerType = null; break;
            case "NestedInstallerFiles": target.NestedInstallerFiles = null; break;
            case "Scope": target.Scope = null; break;
            case "InstallModes": target.InstallModes = null; break;
            case "InstallerSwitches": target.InstallerSwitches = null; break;
            case "InstallerSuccessCodes": target.InstallerSuccessCodes = null; break;
            case "ExpectedReturnCodes": target.ExpectedReturnCodes = null; break;
            case "UpgradeBehavior": target.UpgradeBehavior = null; break;
            case "Commands": target.Commands = null; break;
            case "Protocols": target.Protocols = null; break;
            case "FileExtensions": target.FileExtensions = null; break;
            case "Dependencies": target.Dependencies = null; break;
            case "PackageFamilyName": target.PackageFamilyName = null; break;
            case "ProductCode": target.ProductCode = null; break;
            case "Capabilities": target.Capabilities = null; break;
            case "RestrictedCapabilities": target.RestrictedCapabilities = null; break;
            case "Markets": target.Markets = null; break;
            case "InstallerAbortsTerminal": target.InstallerAbortsTerminal = null; break;
            case "ReleaseDate": target.ReleaseDate = null; break;
            case "InstallLocationRequired": target.InstallLocationRequired = null; break;
            case "RequireExplicitUpgrade": target.RequireExplicitUpgrade = null; break;
            case "DisplayInstallWarnings": target.DisplayInstallWarnings = null; break;
            case "UnsupportedOSArchitectures": target.UnsupportedOSArchitectures = null; break;
            case "UnsupportedArguments": target.UnsupportedArguments = null; break;
            case "AppsAndFeaturesEntries": target.AppsAndFeaturesEntries = null; break;
            case "ElevationRequirement": target.ElevationRequirement = null; break;
            case "InstallationMetadata": target.InstallationMetadata = null; break;
            case "DownloadCommandProhibited": target.DownloadCommandProhibited = null; break;
            case "RepairBehavior": target.RepairBehavior = null; break;
            case "ArchiveBinariesDependOnPath": target.ArchiveBinariesDependOnPath = null; break;
        }
    }

    private static bool HasLocaleField(string field)
        => field is "Publisher" or "PublisherUrl" or "PublisherSupportUrl" or "PrivacyUrl"
            or "Author" or "PackageName" or "PackageUrl" or "License" or "LicenseUrl"
            or "Copyright" or "CopyrightUrl" or "ShortDescription" or "Description"
            or "Tags" or "Agreements" or "ReleaseNotes" or "ReleaseNotesUrl" or "PurchaseUrl"
            or "InstallationNotes" or "Documentations" or "Icons" or "Moniker";

    private static bool HasInstallerField(string field) => InstallerFieldMissing(new InstallerManifest(), field);

    private static string Hash(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "<null>")));
}
