using WinMatsch.Analysis;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// ARP-1: treats AppsAndFeaturesEntries as templates on updates. Any DisplayName or
/// DisplayVersion token equal to the previous PackageVersion is replaced with the new one,
/// while declared installer and ARP identity fields are refreshed from analysis.
/// When installer analysis evidence proposes an unambiguous ARP value for the same installer,
/// that value is preferred over string templating. A value carried verbatim from the previous
/// version that still embeds some <em>other</em> version-looking token (static/unreplaceable)
/// is reported for review instead of being guessed at.
/// </summary>
public sealed class Arp1VersionTemplateRule : IRule
{
    public string Id => RuleCatalogueIds.Arp1;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Templates old ARP version tokens and refreshes declared installer identity from analysis on updates.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        string? oldVersion = context.Previous?.Installer.PackageVersion?.Value;
        string? newVersion = manifest.PackageVersion?.Value;
        if (oldVersion is null || newVersion is null
            || string.Equals(oldVersion, newVersion, StringComparison.Ordinal))
        {
            return;
        }

        if (manifest.AppsAndFeaturesEntries is { } rootEntries)
        {
            ProcessEntries(context, rootEntries, oldVersion, newVersion, installerIndex: null, analysisEntries: null, previousEntries: PreviousRootEntries(context));
        }

        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            Installer? analysisInstaller = FindAnalysisInstaller(context, manifest, installer);
            RefreshInstallerIdentity(context, installer, analysisInstaller, i);
            if (installer.AppsAndFeaturesEntries is not { } entries)
            {
                continue;
            }

            IReadOnlyList<AppsAndFeaturesEntry>? analysisEntries =
                analysisInstaller?.AppsAndFeaturesEntries;
            Installer? previousMatch = null;
            if (context.Previous is { } previous)
            {
                previousMatch = PolicyValues.FindPreviousByEntryKey(
                    manifest,
                    installer,
                    previous.Installer,
                    out bool ambiguous);
                if (ambiguous)
                {
                    context.AddFinding(
                        this,
                        RuleSeverity.Warning,
                        "Several previous installers share this entry's semantic identity; review ARP version templating because no previous entry was selected.",
                        $"Installers[{i}]");
                }
            }

            ProcessEntries(
                context,
                entries,
                oldVersion,
                newVersion,
                i,
                analysisEntries,
                previousMatch?.AppsAndFeaturesEntries);
        }
    }

    private static List<AppsAndFeaturesEntry>? PreviousRootEntries(ManifestContext context)
        => context.Previous?.Installer.AppsAndFeaturesEntries;

    private static Installer? FindAnalysisInstaller(
        ManifestContext context,
        InstallerManifest manifest,
        Installer current)
    {
        InstallerAnalysis? analysis = context.FindEvidence(current.InstallerUrl)?.Analysis;
        if (analysis is null)
        {
            return null;
        }

        if (analysis.Installers.Count == 1)
        {
            return analysis.Installers[0];
        }

        InstallerType? currentType = current.InstallerType ?? manifest.InstallerType;
        Scope? currentScope = current.Scope ?? manifest.Scope;
        LanguageTag? currentLocale = current.InstallerLocale ?? manifest.InstallerLocale;
        Installer[] matches =
        [
            .. analysis.Installers.Where(candidate =>
                (candidate.Architecture is null || candidate.Architecture == current.Architecture)
                && (candidate.InstallerType is null
                    || currentType is null
                    || TypesCompatible(currentType.Value, candidate.InstallerType.Value))
                && (candidate.Scope is null || currentScope is null || candidate.Scope == currentScope)
                && (candidate.InstallerLocale is null
                    || currentLocale is null
                    || candidate.InstallerLocale == currentLocale)),
        ];
        return matches.Length == 1 ? matches[0] : null;
    }

    private void ProcessEntries(
        ManifestContext context,
        List<AppsAndFeaturesEntry> entries,
        string oldVersion,
        string newVersion,
        int? installerIndex,
        IReadOnlyList<AppsAndFeaturesEntry>? analysisEntries,
        List<AppsAndFeaturesEntry>? previousEntries)
    {
        string location = installerIndex is { } index ? $"Installers[{index}]" : "root";
        string fieldPrefix = installerIndex is { } idx ? $"Installers[{idx}]." : string.Empty;
        for (int e = 0; e < entries.Count; e++)
        {
            AppsAndFeaturesEntry entry = entries[e];
            AppsAndFeaturesEntry? analysisEntry = FindAnalysisArpEntry(
                entry,
                e,
                entries.Count,
                analysisEntries);
            entry.DisplayName = Template(
                context, entry.DisplayName, analysisEntry?.DisplayName, oldVersion, newVersion,
                location, $"{fieldPrefix}AppsAndFeaturesEntries[{e}].DisplayName",
                preferAnalysisWithoutOldVersionToken: false);
            entry.DisplayVersion = Template(
                context, entry.DisplayVersion, analysisEntry?.DisplayVersion, oldVersion, newVersion,
                location, $"{fieldPrefix}AppsAndFeaturesEntries[{e}].DisplayVersion",
                preferAnalysisWithoutOldVersionToken: true);
            entry.ProductCode = RefreshDeclaredIdentity(
                context,
                entry.ProductCode,
                analysisEntry?.ProductCode,
                location,
                $"{fieldPrefix}AppsAndFeaturesEntries[{e}].ProductCode");
            entry.UpgradeCode = RefreshDeclaredIdentity(
                context,
                entry.UpgradeCode,
                analysisEntry?.UpgradeCode,
                location,
                $"{fieldPrefix}AppsAndFeaturesEntries[{e}].UpgradeCode");
            entry.InstallerType = RefreshDeclaredIdentity(
                context,
                entry.InstallerType,
                analysisEntry?.InstallerType,
                location,
                $"{fieldPrefix}AppsAndFeaturesEntries[{e}].InstallerType");

            ReviewStaticValue(context, entry.DisplayName, previousEntries, oldVersion, newVersion, location, "DisplayName", static p => p.DisplayName);
            ReviewStaticValue(context, entry.DisplayVersion, previousEntries, oldVersion, newVersion, location, "DisplayVersion", static p => p.DisplayVersion);
        }
    }

    private static AppsAndFeaturesEntry? FindAnalysisArpEntry(
        AppsAndFeaturesEntry current,
        int index,
        int currentCount,
        IReadOnlyList<AppsAndFeaturesEntry>? analyzed)
    {
        if (analyzed is null || analyzed.Count == 0)
        {
            return null;
        }

        AppsAndFeaturesEntry[] identityMatches =
        [
            .. analyzed.Where(candidate =>
                MatchesDeclaredIdentity(current.ProductCode, candidate.ProductCode)
                || MatchesDeclaredIdentity(current.UpgradeCode, candidate.UpgradeCode)
                || MatchesDeclaredIdentity(current.DisplayName, candidate.DisplayName)),
        ];
        if (identityMatches.Length == 1)
        {
            return identityMatches[0];
        }

        return analyzed.Count == currentCount ? analyzed[index] : null;
    }

    private static bool MatchesDeclaredIdentity(string? current, string? analyzed)
        => !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(analyzed)
            && string.Equals(current, analyzed, StringComparison.Ordinal);

    private void RefreshInstallerIdentity(
        ManifestContext context,
        Installer installer,
        Installer? analysisInstaller,
        int installerIndex)
    {
        string location = $"Installers[{installerIndex}]";
        installer.ProductCode = RefreshDeclaredIdentity(
            context,
            installer.ProductCode,
            analysisInstaller?.ProductCode,
            location,
            $"{location}.ProductCode");
        installer.PackageFamilyName = RefreshDeclaredIdentity(
            context,
            installer.PackageFamilyName,
            analysisInstaller?.PackageFamilyName,
            location,
            $"{location}.PackageFamilyName");
    }

    private string? RefreshDeclaredIdentity(
        ManifestContext context,
        string? current,
        string? analyzed,
        string location,
        string fieldPath)
    {
        if (current is null
            || string.IsNullOrWhiteSpace(analyzed)
            || string.Equals(current, analyzed, StringComparison.Ordinal))
        {
            return current;
        }

        AddIdentityEvidence(context, location, fieldPath);
        return analyzed;
    }

    private InstallerType? RefreshDeclaredIdentity(
        ManifestContext context,
        InstallerType? current,
        InstallerType? analyzed,
        string location,
        string fieldPath)
    {
        if (current is null || analyzed is null || current == analyzed)
        {
            return current;
        }

        AddIdentityEvidence(context, location, fieldPath);
        return analyzed;
    }

    private void AddIdentityEvidence(
        ManifestContext context,
        string location,
        string fieldPath)
    {
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            fieldPath,
            "installer analysis identity evidence",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"{location}: {fieldPath} refreshed from installer analysis identity evidence.");
    }

    private static bool TypesCompatible(InstallerType current, InstallerType analyzed)
        => current == analyzed
            || (current is InstallerType.Msi or InstallerType.Wix
                && analyzed is InstallerType.Msi or InstallerType.Wix)
            || (current == InstallerType.Exe
                && analyzed is InstallerType.Inno or InstallerType.Nullsoft or InstallerType.Wix or InstallerType.Burn);

    private string? Template(
        ManifestContext context,
        string? value,
        string? analysisValue,
        string oldVersion,
        string newVersion,
        string location,
        string fieldPath,
        bool preferAnalysisWithoutOldVersionToken)
    {
        if (value is null)
        {
            return value;
        }

        string replacement;
        string source;
        RuleChangeConfidence confidence;
        if (!string.IsNullOrWhiteSpace(analysisValue)
            && (preferAnalysisWithoutOldVersionToken
                || PolicyValues.ContainsVersionToken(value, oldVersion)))
        {
            replacement = analysisValue;
            source = "installer analysis ARP evidence";
            confidence = RuleChangeConfidence.High;
        }
        else if (!PolicyValues.ContainsVersionToken(value, oldVersion))
        {
            return value;
        }
        else
        {
            replacement = PolicyValues.ReplaceVersionToken(value, oldVersion, newVersion);
            source = $"templated previous version token '{oldVersion}' to '{newVersion}'";
            confidence = RuleChangeConfidence.Medium;
        }

        if (string.Equals(replacement, value, StringComparison.Ordinal))
        {
            return value;
        }

        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            fieldPath,
            source,
            confidence);
        context.AddTrace(this, $"{location}: {fieldPath} updated ({source}).");
        return replacement;
    }

    private void ReviewStaticValue(
        ManifestContext context,
        string? value,
        List<AppsAndFeaturesEntry>? previousEntries,
        string oldVersion,
        string newVersion,
        string location,
        string fieldName,
        Func<AppsAndFeaturesEntry, string?> selector)
    {
        if (value is null
            || PolicyValues.ContainsVersionToken(value, newVersion)
            || PolicyValues.ContainsVersionToken(value, oldVersion)
            || !PolicyValues.ContainsVersionLikeToken(value))
        {
            return;
        }

        bool carriedVerbatim = previousEntries is not null
            && previousEntries.Any(p => string.Equals(selector(p), value, StringComparison.Ordinal));
        if (carriedVerbatim)
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"ARP {fieldName} '{value}' was carried verbatim from the previous version and embeds a version-looking token that could not be templated; review whether it is static or stale.",
                location);
        }
    }
}
