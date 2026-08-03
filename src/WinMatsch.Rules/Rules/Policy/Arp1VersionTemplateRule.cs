using WinMatsch.Analysis;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// ARP-1: treats AppsAndFeaturesEntries as templates on updates. Any DisplayName or
/// DisplayVersion token equal to the previous PackageVersion is replaced with the new one.
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

    public string Description => "Templates old version tokens in ARP DisplayName/DisplayVersion on updates.";

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
            ProcessEntries(context, rootEntries, oldVersion, newVersion, installerIndex: null, analysisEntry: null, previousEntries: PreviousRootEntries(context));
        }

        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            if (installer.AppsAndFeaturesEntries is not { } entries)
            {
                continue;
            }

            AppsAndFeaturesEntry? analysisEntry = FindAnalysisArpEntry(context, installer.InstallerUrl);
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

            ProcessEntries(context, entries, oldVersion, newVersion, i, analysisEntry, previousMatch?.AppsAndFeaturesEntries);
        }
    }

    private static List<AppsAndFeaturesEntry>? PreviousRootEntries(ManifestContext context)
        => context.Previous?.Installer.AppsAndFeaturesEntries;

    /// <summary>
    /// The single ARP entry the analyzer proposed for this installer, or null when the
    /// analysis is missing or ambiguous (several proposed installers or entries).
    /// </summary>
    private static AppsAndFeaturesEntry? FindAnalysisArpEntry(ManifestContext context, string? installerUrl)
    {
        InstallerAnalysis? analysis = context.FindEvidence(installerUrl)?.Analysis;
        if (analysis is null || analysis.Installers.Count != 1)
        {
            return null;
        }

        List<AppsAndFeaturesEntry>? proposed = analysis.Installers[0].AppsAndFeaturesEntries;
        return proposed is { Count: 1 } ? proposed[0] : null;
    }

    private void ProcessEntries(
        ManifestContext context,
        List<AppsAndFeaturesEntry> entries,
        string oldVersion,
        string newVersion,
        int? installerIndex,
        AppsAndFeaturesEntry? analysisEntry,
        List<AppsAndFeaturesEntry>? previousEntries)
    {
        string location = installerIndex is { } index ? $"Installers[{index}]" : "root";
        string fieldPrefix = installerIndex is { } idx ? $"Installers[{idx}]." : string.Empty;
        for (int e = 0; e < entries.Count; e++)
        {
            AppsAndFeaturesEntry entry = entries[e];
            entry.DisplayName = Template(
                context, entry.DisplayName, analysisEntry?.DisplayName, oldVersion, newVersion,
                location, $"{fieldPrefix}AppsAndFeaturesEntries[{e}].DisplayName");
            entry.DisplayVersion = Template(
                context, entry.DisplayVersion, analysisEntry?.DisplayVersion, oldVersion, newVersion,
                location, $"{fieldPrefix}AppsAndFeaturesEntries[{e}].DisplayVersion");

            ReviewStaticValue(context, entry.DisplayName, previousEntries, oldVersion, newVersion, location, "DisplayName", static p => p.DisplayName);
            ReviewStaticValue(context, entry.DisplayVersion, previousEntries, oldVersion, newVersion, location, "DisplayVersion", static p => p.DisplayVersion);
        }
    }

    private string? Template(
        ManifestContext context,
        string? value,
        string? analysisValue,
        string oldVersion,
        string newVersion,
        string location,
        string fieldPath)
    {
        if (value is null || !PolicyValues.ContainsVersionToken(value, oldVersion))
        {
            return value;
        }

        string replacement;
        string source;
        RuleChangeConfidence confidence;
        if (!string.IsNullOrWhiteSpace(analysisValue))
        {
            replacement = analysisValue;
            source = "installer analysis ARP evidence";
            confidence = RuleChangeConfidence.High;
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
