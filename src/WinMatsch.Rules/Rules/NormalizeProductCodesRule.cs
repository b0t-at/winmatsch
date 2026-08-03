using System.Globalization;
using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0006: normalizes ProductCode and UpgradeCode values that parse as GUIDs to the canonical
/// MSI form: uppercase hexadecimal wrapped in braces, <c>{XXXXXXXX-XXXX-...}</c>. Values that
/// are not GUIDs (Inno Setup ProductCodes like <c>MyApp_is1</c>) are left untouched. Applies to
/// the root and per-installer <c>ProductCode</c> and to both codes in every
/// AppsAndFeaturesEntry.
/// </summary>
public sealed class NormalizeProductCodesRule : IRule
{
    public string Id => RuleIds.NormalizeProductCodes;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Normalizes GUID product and upgrade codes to uppercase-in-braces form.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        NormalizeFields(context, manifest, string.Empty);
        if (manifest.Installers is { } installers)
        {
            for (int i = 0; i < installers.Count; i++)
            {
                NormalizeFields(context, installers[i], $"Installers[{i}].");
            }
        }
    }

    /// <summary>Returns the canonical form when the value parses as a GUID; otherwise the value unchanged.</summary>
    internal static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return Guid.TryParse(value.Trim(), out Guid guid)
            ? guid.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()
            : value;
    }

    private void NormalizeFields(ManifestContext context, InstallerFieldsBase fields, string pathPrefix)
    {
        fields.ProductCode = NormalizeAndTrace(context, fields.ProductCode, $"{pathPrefix}ProductCode");
        if (fields.AppsAndFeaturesEntries is { } entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                AppsAndFeaturesEntry entry = entries[i];
                entry.ProductCode = NormalizeAndTrace(context, entry.ProductCode, $"{pathPrefix}AppsAndFeaturesEntries[{i}].ProductCode");
                entry.UpgradeCode = NormalizeAndTrace(context, entry.UpgradeCode, $"{pathPrefix}AppsAndFeaturesEntries[{i}].UpgradeCode");
            }
        }
    }

    private string? NormalizeAndTrace(ManifestContext context, string? value, string path)
    {
        string? normalized = Normalize(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            context.AddTrace(this, $"{path}: normalized '{value}' to '{normalized}'.");
        }

        return normalized;
    }
}
