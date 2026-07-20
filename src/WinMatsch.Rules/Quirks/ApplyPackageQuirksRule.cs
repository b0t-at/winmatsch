using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0201: applies the data-driven per-package quirks from <see cref="QuirkPack"/>. Currently
/// supported quirk: prefer an analyzer evidence property (for example the MSI
/// summary-information <c>Comments</c> value for Google.Chrome) as the AppsAndFeaturesEntries
/// <c>DisplayVersion</c> of the installer the evidence belongs to. Runs before the generic
/// normalization passes, so WM0003 still removes the value again if it turns out to equal the
/// PackageVersion.
/// </summary>
public sealed class ApplyPackageQuirksRule : IRule
{
    public string Id => RuleIds.ApplyPackageQuirks;

    public RuleCategory Category => RuleCategory.Quirk;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Applies data-driven per-package quirks from the quirk pack.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? packageIdentifier = context.Manifests.Installer.PackageIdentifier?.Value;
        if (packageIdentifier is null || !QuirkPack.Quirks.TryGetValue(packageIdentifier, out PackageQuirks? quirks))
        {
            return;
        }

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
                    context.AddTrace(this, $"Installers[{i}]: set AppsAndFeaturesEntries DisplayVersion from evidence property '{propertyName}'.");
                }
            }
        }
    }
}
