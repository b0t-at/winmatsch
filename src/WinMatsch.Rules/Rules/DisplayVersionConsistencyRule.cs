using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0101: warns when AppsAndFeaturesEntries DisplayVersion usage is inconsistent across
/// installers — some installers declare one while others do not, or the declared values
/// disagree. Additionally emits an info-level finding when a DisplayVersion equals the
/// PackageVersion (redundant; WM0003 removes exact matches during normalization, so this only
/// surfaces when that rule is disabled or the value reappears later).
/// </summary>
public sealed class DisplayVersionConsistencyRule : IRule
{
    public string Id => RuleIds.DisplayVersionConsistency;

    public RuleCategory Category => RuleCategory.Validation;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Warns when ARP DisplayVersion values are inconsistent across installers.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count == 0)
        {
            return;
        }

        var declared = new List<string>();
        int installersWithValue = 0;
        foreach (Installer installer in installers)
        {
            List<AppsAndFeaturesEntry>? entries = EffectiveInstallerValues.GetAppsAndFeaturesEntries(manifest, installer);
            bool hasValue = false;
            if (entries is not null)
            {
                foreach (AppsAndFeaturesEntry entry in entries)
                {
                    if (entry.DisplayVersion is { } displayVersion)
                    {
                        hasValue = true;
                        if (!declared.Contains(displayVersion, StringComparer.Ordinal))
                        {
                            declared.Add(displayVersion);
                        }
                    }
                }
            }

            if (hasValue)
            {
                installersWithValue++;
            }
        }

        if (installersWithValue == 0)
        {
            return;
        }

        if (installersWithValue < installers.Count)
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"{installersWithValue} of {installers.Count} installers declare an AppsAndFeaturesEntries DisplayVersion; the others do not. Mixed usage confuses upgrade detection.");
        }

        if (declared.Count > 1)
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"AppsAndFeaturesEntries DisplayVersion values disagree across installers: {string.Join(", ", declared)}.");
        }

        string? packageVersion = manifest.PackageVersion?.Value;
        if (packageVersion is not null && declared.Contains(packageVersion, StringComparer.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Info,
                $"AppsAndFeaturesEntries DisplayVersion '{packageVersion}' equals the PackageVersion and is redundant; consider removing it.");
        }
    }
}
