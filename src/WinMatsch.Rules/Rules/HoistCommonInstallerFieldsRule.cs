using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0001: any <see cref="InstallerFieldsBase"/> property whose value is present and
/// deep-equal on every installer moves to the installer-manifest root and is cleared on each
/// installer. When the root already holds the same value the redundant per-installer copies
/// are cleared. Properties that differ between installers (for example
/// <c>AppsAndFeaturesEntries</c> with per-architecture product codes) stay per-installer, and
/// a root value conflicting with the common installer value is left for WM0002 to resolve.
/// On an update, a differing property the previous version declared at the root is restored
/// there as the default (see <see cref="RestorePreviousRootValue"/>).
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
                RestorePreviousRootValue(context, manifest, installers, accessor);
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

    /// <summary>
    /// On an update, WM0002 pushes a root default down whenever one installer overrides it, and
    /// the equality hoist above cannot bring it back. When the previous version declared the
    /// field at the root, the default is restored so the manifest keeps its accepted layout —
    /// winget-pkgs otherwise reports the root property as missing (edde746.Plezy Dependencies).
    /// Only lossless when every installer carries its own value, so none starts inheriting.
    /// </summary>
    private void RestorePreviousRootValue(
        ManifestContext context,
        InstallerManifest manifest,
        List<Installer> installers,
        InstallerFieldAccessor accessor)
    {
        if (context.Previous?.Installer is not { } previousManifest
            || accessor.Get(previousManifest) is not { } previousRoot
            || accessor.Get(manifest) is not null
            || installers.Any(installer => accessor.Get(installer) is null))
        {
            return;
        }

        Installer[] matching =
        [
            .. installers.Where(installer => accessor.ValueEquals(previousRoot, accessor.Get(installer)!)),
        ];
        if (matching.Length == 0)
        {
            return;
        }

        accessor.Set(manifest, accessor.Get(matching[0]));
        foreach (Installer installer in matching)
        {
            accessor.Set(installer, null);
        }

        context.AddTrace(this,
            $"Restored the previous version's root {accessor.Name}; {installers.Count - matching.Length} installer(s) keep their own value.");
    }

    private static void ClearOnAllInstallers(List<Installer> installers, InstallerFieldAccessor accessor)
    {
        foreach (Installer installer in installers)
        {
            accessor.Set(installer, null);
        }
    }
}
