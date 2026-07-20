using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0103: reports invalid nested-installer combinations: <c>NestedInstallerType</c> set while
/// the effective <c>InstallerType</c> is not <c>zip</c>, and <c>NestedInstallerFiles</c> set
/// without a <c>NestedInstallerType</c>. Effective values look through root defaults, and a
/// manifest without installer entries is checked at the root.
/// </summary>
public sealed class InstallerTypeConsistencyRule : IRule
{
    public string Id => RuleIds.InstallerTypeConsistency;

    public RuleCategory Category => RuleCategory.Validation;

    public RuleSeverity Severity => RuleSeverity.Error;

    public string Description => "Reports invalid InstallerType / NestedInstallerType combinations.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is { Count: > 0 } installers)
        {
            for (int i = 0; i < installers.Count; i++)
            {
                Installer installer = installers[i];
                Check(context,
                    EffectiveInstallerValues.GetInstallerType(manifest, installer),
                    EffectiveInstallerValues.GetNestedInstallerType(manifest, installer),
                    EffectiveInstallerValues.GetNestedInstallerFiles(manifest, installer),
                    $"Installers[{i}]");
            }
        }
        else
        {
            Check(context, manifest.InstallerType, manifest.NestedInstallerType, manifest.NestedInstallerFiles, "InstallerManifest");
        }
    }

    private void Check(ManifestContext context, InstallerType? installerType, InstallerType? nestedInstallerType, List<NestedInstallerFile>? nestedInstallerFiles, string path)
    {
        if (nestedInstallerType is not null && installerType != InstallerType.Zip)
        {
            context.AddFinding(this,
                $"NestedInstallerType is set but the installer type is {installerType?.ToString() ?? "not set"}; nested installers are only valid for zip installers.",
                $"{path}.NestedInstallerType");
        }

        if (nestedInstallerFiles is not null && nestedInstallerType is null)
        {
            context.AddFinding(this,
                "NestedInstallerFiles is set without a NestedInstallerType.",
                $"{path}.NestedInstallerFiles");
        }
    }
}
