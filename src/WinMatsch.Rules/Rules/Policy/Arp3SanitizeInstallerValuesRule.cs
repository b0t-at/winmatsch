using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// ARP-3: rejects garbage values that raw installer extraction leaks into
/// AppsAndFeaturesEntries and InstallationMetadata: unexpanded NSIS/Inno variables
/// (<c>$INSTDIR</c>, <c>$_44_</c>), <c>ms-resource:</c> references, control/non-printable
/// characters, random <c>%Temp%</c> paths, and empty mappings. Parents that become empty are
/// pruned so no <c>- {}</c> survives serialization.
/// </summary>
public sealed class Arp3SanitizeInstallerValuesRule : IRule
{
    public string Id => RuleCatalogueIds.Arp3;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Drops unresolved installer variables and garbage from ARP and InstallationMetadata values.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.AppsAndFeaturesEntries is { } rootEntries)
        {
            SanitizeEntries(context, rootEntries, "root");
            if (rootEntries.Count == 0)
            {
                manifest.AppsAndFeaturesEntries = null;
            }
        }

        if (manifest.InstallationMetadata is not null)
        {
            manifest.InstallationMetadata = SanitizeInstallationMetadata(context, manifest.InstallationMetadata, "root");
        }

        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            string location = $"Installers[{i}]";
            if (installer.AppsAndFeaturesEntries is { } entries)
            {
                SanitizeEntries(context, entries, location);
                if (entries.Count == 0)
                {
                    installer.AppsAndFeaturesEntries = null;
                }
            }

            if (installer.InstallationMetadata is not null)
            {
                installer.InstallationMetadata = SanitizeInstallationMetadata(context, installer.InstallationMetadata, location);
            }
        }
    }

    private void SanitizeEntries(ManifestContext context, List<AppsAndFeaturesEntry> entries, string location)
    {
        for (int e = entries.Count - 1; e >= 0; e--)
        {
            AppsAndFeaturesEntry entry = entries[e];
            entry.DisplayName = Sanitize(context, entry.DisplayName, location, "DisplayName");
            entry.DisplayVersion = Sanitize(context, entry.DisplayVersion, location, "DisplayVersion");
            entry.Publisher = Sanitize(context, entry.Publisher, location, "Publisher");
            entry.ProductCode = Sanitize(context, entry.ProductCode, location, "ProductCode");
            entry.UpgradeCode = Sanitize(context, entry.UpgradeCode, location, "UpgradeCode");

            if (IsEmpty(entry))
            {
                entries.RemoveAt(e);
                context.AddTrace(this, $"{location}: removed empty AppsAndFeaturesEntries[{e}].");
            }
        }
    }

    private InstallationMetadata? SanitizeInstallationMetadata(
        ManifestContext context,
        InstallationMetadata metadata,
        string location)
    {
        metadata.DefaultInstallLocation = Sanitize(
            context, metadata.DefaultInstallLocation, location, "InstallationMetadata.DefaultInstallLocation");

        if (metadata.Files is { Count: 0 })
        {
            metadata.Files = null;
        }

        if (metadata.DefaultInstallLocation is null && metadata.Files is null)
        {
            context.AddTrace(this, $"{location}: removed empty InstallationMetadata.");
            return null;
        }

        return metadata;
    }

    private string? Sanitize(ManifestContext context, string? value, string location, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        string? reason = Classify(value);
        if (reason is null)
        {
            return value;
        }

        context.AddFinding(this, RuleSeverity.Warning,
            $"Dropped {fieldName} value: {reason}.",
            location);
        return null;
    }

    /// <summary>The rejection reason for a garbage value, or null when the value is acceptable.</summary>
    private static string? Classify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "the value is empty or whitespace";
        }

        if (ContainsInstallerVariableMarker(value))
        {
            return "it contains an unexpanded NSIS/Inno installer variable";
        }

        if (value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return "it is an unresolved ms-resource reference";
        }

        if (PolicyValues.ContainsNonPrintable(value))
        {
            return "it contains control or non-printable characters";
        }

        if (IsTempPath(value))
        {
            return "it points into %Temp% with a per-build random suffix";
        }

        return null;
    }

    private static bool ContainsInstallerVariableMarker(string value)
    {
        for (int i = 0; i < value.Length - 1; i++)
        {
            if (value[i] != '$')
            {
                continue;
            }

            char next = value[i + 1];
            if (next is '_' or '{' || char.IsAsciiLetterUpper(next))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTempPath(string value)
        => value.Contains("%Temp%", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%tmp%", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmpty(AppsAndFeaturesEntry entry)
        => entry.DisplayName is null
            && entry.Publisher is null
            && entry.DisplayVersion is null
            && entry.ProductCode is null
            && entry.UpgradeCode is null
            && entry.InstallerType is null;
}
