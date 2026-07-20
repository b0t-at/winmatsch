using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0005: removes exact duplicate installers — same effective Architecture, InstallerType and
/// Scope (looking through root defaults) and the same InstallerUrl (case-insensitive) — keeping
/// the first occurrence. Installers that share the key but point at different URLs are left in
/// place; WM0102 reports those as an error finding.
/// </summary>
public sealed class RemoveDuplicateInstallersRule : IRule
{
    public string Id => RuleIds.RemoveDuplicateInstallers;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Removes installers that duplicate an earlier one in architecture, type, scope and URL.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count < 2)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (index < installers.Count)
        {
            Installer installer = installers[index];
            string key = $"{EffectiveInstallerValues.GetEntryKey(manifest, installer)}|{installer.InstallerUrl}";
            if (seen.Add(key))
            {
                index++;
            }
            else
            {
                installers.RemoveAt(index);
                context.AddTrace(this, $"Removed duplicate installer ({key}); an identical entry appears earlier.");
            }
        }
    }
}
