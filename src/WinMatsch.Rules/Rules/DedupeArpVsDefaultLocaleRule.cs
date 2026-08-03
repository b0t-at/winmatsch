using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0003: removes AppsAndFeaturesEntries values that merely repeat what WinGet already knows:
/// <c>DisplayName</c> equal to the default locale's <c>PackageName</c>, <c>Publisher</c> equal
/// to the default locale's <c>Publisher</c>, and <c>DisplayVersion</c> equal to the
/// <c>PackageVersion</c>. Entries that end up with all fields null are dropped, and a list that
/// becomes empty is removed. Applies to the manifest root and to every installer.
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
        string? packageName = context.Manifests.DefaultLocale.PackageName;
        string? publisher = context.Manifests.DefaultLocale.Publisher;
        string? packageVersion = manifest.PackageVersion?.Value;

        Dedupe(context, manifest, "AppsAndFeaturesEntries", packageName, publisher, packageVersion);
        if (manifest.Installers is { } installers)
        {
            for (int i = 0; i < installers.Count; i++)
            {
                Dedupe(context, installers[i], $"Installers[{i}].AppsAndFeaturesEntries", packageName, publisher, packageVersion);
            }
        }
    }

    private void Dedupe(ManifestContext context, InstallerFieldsBase fields, string path, string? packageName, string? publisher, string? packageVersion)
    {
        List<AppsAndFeaturesEntry>? entries = fields.AppsAndFeaturesEntries;
        if (entries is null)
        {
            return;
        }

        foreach (AppsAndFeaturesEntry entry in entries)
        {
            if (entry.DisplayName is not null && string.Equals(entry.DisplayName, packageName, StringComparison.Ordinal))
            {
                entry.DisplayName = null;
                context.AddTrace(this, $"{path}: dropped DisplayName equal to the default locale PackageName.");
            }

            if (entry.Publisher is not null && string.Equals(entry.Publisher, publisher, StringComparison.Ordinal))
            {
                entry.Publisher = null;
                context.AddTrace(this, $"{path}: dropped Publisher equal to the default locale Publisher.");
            }

            if (entry.DisplayVersion is not null && string.Equals(entry.DisplayVersion, packageVersion, StringComparison.Ordinal))
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
}
