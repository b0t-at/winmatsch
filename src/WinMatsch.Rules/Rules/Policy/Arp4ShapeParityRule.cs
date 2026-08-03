using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// ARP-4: compares the AppsAndFeaturesEntries shape (which fields each entry declares, and how
/// many entries exist) against the previous merged version. Adding or removing ARP keys between
/// versions trips winget-pkgs' Manifest-Metadata-Consistency moderation in both directions, so
/// a shape difference requires either mirroring the previous layout or an explicit
/// package-override annotation (<c>PolicyAnnotation</c> with id <c>ARP-4</c>). Findings only —
/// this rule never mutates.
/// </summary>
public sealed class Arp4ShapeParityRule : IRule
{
    private readonly OverridePackSet _overridePacks;

    public Arp4ShapeParityRule(OverridePackSet? overridePacks = null)
    {
        _overridePacks = overridePacks ?? OverridePackSet.Empty;
    }

    public string Id => RuleCatalogueIds.Arp4;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Requires ARP entry shape parity with the previous merged version or an explicit override.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Previous is not { } previous)
        {
            return;
        }

        List<string> currentShape = CollectShape(context.Manifests.Installer);
        List<string> previousShape = CollectShape(previous.Installer);
        if (currentShape.SequenceEqual(previousShape, StringComparer.Ordinal))
        {
            return;
        }

        if (HasExplicitOverride(context))
        {
            context.AddTrace(this,
                "ARP shape differs from the previous version, but a package override annotation explicitly allows the change.");
            return;
        }

        string current = currentShape.Count == 0 ? "(none)" : string.Join("; ", currentShape);
        string before = previousShape.Count == 0 ? "(none)" : string.Join("; ", previousShape);
        context.AddFinding(this, RuleSeverity.Warning,
            $"AppsAndFeaturesEntries shape changed vs the previous version: previous [{before}] vs current [{current}]. Mirror the previous shape or add an explicit ARP-4 package override annotation.");
    }

    private bool HasExplicitOverride(ManifestContext context)
        => _overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? pack)
            && pack is not null
            && pack.Policies.Any(p => string.Equals(p.Id, RuleCatalogueIds.Arp4, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The package-level ARP shape: one sorted field-set signature per effective entry, sorted
    /// so that installer reordering does not register as a shape change.
    /// </summary>
    private static List<string> CollectShape(InstallerManifest manifest)
    {
        var signatures = new List<string>();
        if (manifest.Installers is { } installers)
        {
            foreach (Installer installer in installers)
            {
                List<AppsAndFeaturesEntry>? entries = EffectiveInstallerValues.GetAppsAndFeaturesEntries(manifest, installer);
                if (entries is null)
                {
                    continue;
                }

                foreach (AppsAndFeaturesEntry entry in entries)
                {
                    signatures.Add(Signature(entry));
                }
            }
        }

        signatures.Sort(StringComparer.Ordinal);
        return signatures;
    }

    private static string Signature(AppsAndFeaturesEntry entry)
    {
        var fields = new List<string>(6);
        if (entry.DisplayName is not null)
        {
            fields.Add(nameof(entry.DisplayName));
        }

        if (entry.Publisher is not null)
        {
            fields.Add(nameof(entry.Publisher));
        }

        if (entry.DisplayVersion is not null)
        {
            fields.Add(nameof(entry.DisplayVersion));
        }

        if (entry.ProductCode is not null)
        {
            fields.Add(nameof(entry.ProductCode));
        }

        if (entry.UpgradeCode is not null)
        {
            fields.Add(nameof(entry.UpgradeCode));
        }

        if (entry.InstallerType is not null)
        {
            fields.Add(nameof(entry.InstallerType));
        }

        return fields.Count == 0 ? "(empty)" : string.Join(",", fields);
    }
}
