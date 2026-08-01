using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0102: reports an error when two installers match WinGet's duplicate-installer relation,
/// looking through root defaults and treating absent scope/locale as wildcards.
/// </summary>
public sealed class DuplicateInstallerEntriesRule : IRule
{
    public string Id => RuleIds.DuplicateInstallerEntries;

    public RuleCategory Category => RuleCategory.Validation;

    public RuleSeverity Severity => RuleSeverity.Error;

    public string Description => "Reports installers that collide under WinGet's duplicate-installer relation.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count < 2)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            int firstIndex = -1;
            for (int candidate = 0; candidate < i; candidate++)
            {
                if (InstallerDuplicateRelation.AreDuplicates(
                        manifest,
                        installers[candidate],
                        installers[i]))
                {
                    firstIndex = candidate;
                    break;
                }
            }

            if (firstIndex >= 0)
            {
                context.AddFinding(this,
                    $"Installers[{firstIndex}] and Installers[{i}] collide under WinGet's effective architecture, installer type, nested installer type, scope, and locale relation.",
                    $"Installers[{i}]");
            }
        }
    }
}
