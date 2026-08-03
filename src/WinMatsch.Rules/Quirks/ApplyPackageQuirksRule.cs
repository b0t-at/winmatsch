using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Rules;

/// <summary>
/// WM0201: applies the data-driven per-package quirks from override packs. Currently
/// supported quirk: prefer an analyzer evidence property (for example the MSI
/// summary-information <c>Comments</c> value for Google.Chrome) as the AppsAndFeaturesEntries
/// <c>DisplayVersion</c> of the installer the evidence belongs to. Runs before the generic
/// normalization passes, so WM0003 still removes the value again if it turns out to equal the
/// PackageVersion.
/// </summary>
public sealed class ApplyPackageQuirksRule : IRule
{
    private readonly OverridePackSet _overridePacks;

    public ApplyPackageQuirksRule()
        : this(OverridePackSet.BuiltIn)
    {
    }

    public ApplyPackageQuirksRule(OverridePackSet overridePacks)
    {
        ArgumentNullException.ThrowIfNull(overridePacks);
        _overridePacks = overridePacks;
    }

    public string Id => RuleIds.ApplyPackageQuirks;

    public RuleCategory Category => RuleCategory.Quirk;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Applies data-driven per-package quirks from the quirk pack.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? pack)
            || pack is null)
        {
            return;
        }

        PackageQuirks quirks = pack.Quirks;
        if (quirks.DisplayVersionFromEvidenceProperty is { } propertyName)
        {
            ApplyDisplayVersionFromEvidence(context, propertyName);
        }
    }

    private void ApplyDisplayVersionFromEvidence(ManifestContext context, string propertyName)
    {
        List<Installer>? installers = context.Manifests.Installer.Installers;
        if (installers is null)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            InstallerEvidence? evidence = context.FindEvidence(installer.InstallerUrl);
            if (evidence?.Properties is null
                || !evidence.Properties.TryGetValue(propertyName, out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            List<AppsAndFeaturesEntry> entries = installer.AppsAndFeaturesEntries ??= [];
            if (entries.Count == 0)
            {
                entries.Add(new AppsAndFeaturesEntry());
            }

            foreach (AppsAndFeaturesEntry entry in entries)
            {
                if (!string.Equals(entry.DisplayVersion, value, StringComparison.Ordinal))
                {
                    entry.DisplayVersion = value;
                    int entryIndex = entries.IndexOf(entry);
                    string fieldPath = $"Installers[{i}].AppsAndFeaturesEntries[{entryIndex}].DisplayVersion";
                    context.AddChangeEvidence(
                        this,
                        ManifestContext.GetInstallerManifestPath(context.Manifests),
                        fieldPath,
                        $"installer evidence property '{propertyName}' from {installer.InstallerUrl}",
                        RuleChangeConfidence.High);
                    context.AddTrace(this, $"Installers[{i}]: set AppsAndFeaturesEntries DisplayVersion from evidence property '{propertyName}'.");
                }
            }
        }
    }
}
