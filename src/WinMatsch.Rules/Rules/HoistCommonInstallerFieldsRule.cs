using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0001: any <see cref="InstallerFieldsBase"/> property whose value is present and
/// deep-equal on every installer moves to the installer-manifest root and is cleared on each
/// installer. When the root already holds the same value the redundant per-installer copies
/// are cleared. Properties that differ between installers (for example
/// <c>AppsAndFeaturesEntries</c> with per-architecture product codes) stay per-installer, and
/// a root value conflicting with the common installer value is left for WM0002 to resolve.
/// </summary>
public sealed class HoistCommonInstallerFieldsRule : IRule
{
    public string Id => RuleIds.HoistCommonInstallerFields;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Moves installer fields shared by all installers to the manifest root.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count == 0)
        {
            return;
        }

        foreach (InstallerFieldAccessor accessor in InstallerFieldAccessors.All)
        {
            object? common = accessor.Get(installers[0]);
            if (common is null)
            {
                continue;
            }

            bool allEqual = true;
            for (int i = 1; i < installers.Count; i++)
            {
                if (accessor.Get(installers[i]) is not { } value || !accessor.ValueEquals(common, value))
                {
                    allEqual = false;
                    break;
                }
            }

            if (!allEqual)
            {
                continue;
            }

            object? rootValue = accessor.Get(manifest);
            if (rootValue is null)
            {
                accessor.Set(manifest, common);
                ClearOnAllInstallers(installers, accessor);
                context.AddTrace(this, $"Hoisted {accessor.Name} shared by all {installers.Count} installer(s) to the manifest root.");
            }
            else if (accessor.ValueEquals(rootValue, common))
            {
                ClearOnAllInstallers(installers, accessor);
                context.AddTrace(this, $"Removed per-installer {accessor.Name} values that duplicate the manifest root value.");
            }

            // Root value differs from the common installer value: a conflict, WM0002's territory.
        }
    }

    private static void ClearOnAllInstallers(List<Installer> installers, InstallerFieldAccessor accessor)
    {
        foreach (Installer installer in installers)
        {
            accessor.Set(installer, null);
        }
    }
}
