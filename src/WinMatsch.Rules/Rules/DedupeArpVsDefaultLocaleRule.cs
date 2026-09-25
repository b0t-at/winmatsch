using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0003: removes AppsAndFeaturesEntries values that merely repeat what WinGet already knows:
/// <c>DisplayName</c> equal to the default locale's <c>PackageName</c>, <c>Publisher</c> equal
/// to the default locale's <c>Publisher</c>, and <c>DisplayVersion</c> equal to the
/// <c>PackageVersion</c>. Entries that end up with all fields null are dropped, and a list that
/// becomes empty is removed. Applies to the manifest root and to every installer. On an update,
/// a field the previous version's entries declared is kept: removing it changes the ARP shape,
/// which winget-pkgs flags as Manifest-Metadata-Consistency and ARP-4 reports (Lando.Lando).
/// </summary>
public sealed class DedupeArpVsDefaultLocaleRule : IRule
{
    public string Id => RuleIds.DedupeArpVsDefaultLocale;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Drops AppsAndFeaturesEntries values that duplicate the default locale or the package version.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        var redundant = new RedundantValues(
            context.Manifests.DefaultLocale.PackageName,
            context.Manifests.DefaultLocale.Publisher,
            manifest.PackageVersion?.Value,
            PreviousFields.From(context.Previous?.Installer));

        Dedupe(context, manifest, "AppsAndFeaturesEntries", redundant);
        if (manifest.Installers is { } installers)
        {
            for (int i = 0; i < installers.Count; i++)
            {
                Dedupe(context, installers[i], $"Installers[{i}].AppsAndFeaturesEntries", redundant);
            }
        }
    }

    private void Dedupe(ManifestContext context, InstallerFieldsBase fields, string path, RedundantValues redundant)
    {
        List<AppsAndFeaturesEntry>? entries = fields.AppsAndFeaturesEntries;
        if (entries is null)
        {
            return;
        }

        foreach (AppsAndFeaturesEntry entry in entries)
        {
            if (entry.DisplayName is not null
                && !redundant.Previous.DisplayName
                && string.Equals(entry.DisplayName, redundant.PackageName, StringComparison.Ordinal))
            {
                entry.DisplayName = null;
                context.AddTrace(this, $"{path}: dropped DisplayName equal to the default locale PackageName.");
            }

            if (entry.Publisher is not null
                && !redundant.Previous.Publisher
                && string.Equals(entry.Publisher, redundant.Publisher, StringComparison.Ordinal))
            {
                entry.Publisher = null;
                context.AddTrace(this, $"{path}: dropped Publisher equal to the default locale Publisher.");
            }

            if (entry.DisplayVersion is not null
                && !redundant.Previous.DisplayVersion
                && string.Equals(entry.DisplayVersion, redundant.PackageVersion, StringComparison.Ordinal))
            {
                entry.DisplayVersion = null;
                context.AddTrace(this, $"{path}: dropped DisplayVersion equal to the PackageVersion.");
            }
        }

        int removed = entries.RemoveAll(static e =>
            e.DisplayName is null && e.Publisher is null && e.DisplayVersion is null
            && e.ProductCode is null && e.UpgradeCode is null && e.InstallerType is null);
        if (removed > 0)
        {
            context.AddTrace(this, $"{path}: removed {removed} empty entry(ies).");
        }

        if (entries.Count == 0)
        {
            fields.AppsAndFeaturesEntries = null;
            context.AddTrace(this, $"{path}: removed the empty list.");
        }
    }

    private readonly record struct RedundantValues(
        string? PackageName,
        string? Publisher,
        string? PackageVersion,
        PreviousFields Previous);

    private readonly record struct PreviousFields(bool DisplayName, bool Publisher, bool DisplayVersion)
    {
        public static PreviousFields From(InstallerManifest? previous)
        {
            if (previous is null)
            {
                return default;
            }

            AppsAndFeaturesEntry[] entries =
            [
                .. previous.AppsAndFeaturesEntries ?? [],
                .. (previous.Installers ?? []).SelectMany(static installer => installer.AppsAndFeaturesEntries ?? []),
            ];
            return new(
                entries.Any(static entry => entry.DisplayName is not null),
                entries.Any(static entry => entry.Publisher is not null),
                entries.Any(static entry => entry.DisplayVersion is not null));
        }
    }
}
