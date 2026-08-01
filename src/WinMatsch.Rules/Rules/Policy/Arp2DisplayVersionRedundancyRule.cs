using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// ARP-2: removes AppsAndFeaturesEntries DisplayVersion values that are redundant (equal to the
/// PackageVersion) and drops values that a supplied repository-index scan proved are already
/// declared by another version of the package (the "DisplayVersion overlap" pipeline killer).
/// The overlap check runs only on supplied <see cref="PolicyEvidence.ExistingDisplayVersions"/>
/// — the rule never guesses at index contents.
/// </summary>
public sealed class Arp2DisplayVersionRedundancyRule : IRule
{
    private readonly PolicyEvidence _evidence;

    public Arp2DisplayVersionRedundancyRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Arp2;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Removes redundant or index-overlapping ARP DisplayVersion values.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        string? packageVersion = manifest.PackageVersion?.Value;

        if (manifest.AppsAndFeaturesEntries is { } rootEntries)
        {
            Process(context, rootEntries, packageVersion, "root", string.Empty);
        }

        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            if (installers[i].AppsAndFeaturesEntries is { } entries)
            {
                Process(context, entries, packageVersion, $"Installers[{i}]", $"Installers[{i}].");
            }
        }
    }

    private void Process(
        ManifestContext context,
        List<AppsAndFeaturesEntry> entries,
        string? packageVersion,
        string location,
        string fieldPrefix)
    {
        for (int e = 0; e < entries.Count; e++)
        {
            string? displayVersion = entries[e].DisplayVersion;
            if (displayVersion is null)
            {
                continue;
            }

            if (packageVersion is not null
                && string.Equals(displayVersion, packageVersion, StringComparison.Ordinal))
            {
                Drop(context, entries[e], e, location, fieldPrefix,
                    $"DisplayVersion '{displayVersion}' equals the PackageVersion and is redundant");
                continue;
            }

            bool overlaps = _evidence.ExistingDisplayVersions
                .Any(existing => string.Equals(existing, displayVersion, StringComparison.Ordinal));
            if (overlaps)
            {
                Drop(context, entries[e], e, location, fieldPrefix,
                    $"DisplayVersion '{displayVersion}' is already declared by another version of this package (supplied index evidence); a static value would collide with the index");
                context.AddFinding(this, RuleSeverity.Warning,
                    $"Dropped ARP DisplayVersion '{displayVersion}': it overlaps a DisplayVersion declared by an existing version of this package.",
                    location);
            }
        }
    }

    private void Drop(
        ManifestContext context,
        AppsAndFeaturesEntry entry,
        int entryIndex,
        string location,
        string fieldPrefix,
        string reason)
    {
        entry.DisplayVersion = null;
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            $"{fieldPrefix}AppsAndFeaturesEntries[{entryIndex}].DisplayVersion",
            reason,
            RuleChangeConfidence.High);
        context.AddTrace(this, $"{location}: removed AppsAndFeaturesEntries[{entryIndex}].DisplayVersion — {reason}.");
    }
}
