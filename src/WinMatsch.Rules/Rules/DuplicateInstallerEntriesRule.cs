using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0102: reports an error when two installers share the same effective
/// Architecture+InstallerType+Scope key (looking through root defaults). Exact duplicates
/// (same key and same URL) are removed by WM0005 during normalization; what remains here is
/// the "Duplicate installer entry found" class of failure that kills winget-pkgs pipeline
/// runs, usually caused by two different assets collapsing onto one architecture.
/// </summary>
public sealed class DuplicateInstallerEntriesRule : IRule
{
    public string Id => RuleIds.DuplicateInstallerEntries;

    public RuleCategory Category => RuleCategory.Validation;

    public RuleSeverity Severity => RuleSeverity.Error;

    public string Description => "Reports installers that collide on architecture, installer type and scope.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count < 2)
        {
            return;
        }

        var firstIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < installers.Count; i++)
        {
            string key = EffectiveInstallerValues.GetEntryKey(manifest, installers[i]);
            if (!firstIndexByKey.TryGetValue(key, out int firstIndex))
            {
                firstIndexByKey.Add(key, i);
                continue;
            }

            if (reported.Add(key))
            {
                context.AddFinding(this,
                    $"Installers[{firstIndex}] and Installers[{i}] share the same Architecture+InstallerType+Scope ({key.Replace('|', '/')}); winget rejects manifests with duplicate installer entries.",
                    $"Installers[{i}]");
            }
        }
    }
}
