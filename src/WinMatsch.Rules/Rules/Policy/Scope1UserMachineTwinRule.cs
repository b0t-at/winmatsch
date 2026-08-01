using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// SCOPE-1: when two installer entries share the same URL and differ only in recognized
/// per-user vs per-machine switch tokens (<c>/CURRENTUSER</c> vs <c>/ALLUSERS</c>,
/// <c>ALLUSERS=1</c> vs <c>MSIINSTALLPERUSER=1</c>), each twin gets its per-installer
/// <c>Scope</c> and the manifest root stays scope-free (the Pandoc layout). Assignment is
/// deliberately conservative: it only fires when both twins carry an unambiguous, opposite
/// token, and it never overwrites an explicit per-installer scope.
/// </summary>
public sealed class Scope1UserMachineTwinRule : IRule
{
    private static readonly string[] _userTokens = ["/CURRENTUSER", "MSIINSTALLPERUSER=1", "ALLUSERS=\"\"", "ALLUSERS=2"];
    private static readonly string[] _machineTokens = ["/ALLUSERS", "ALLUSERS=1"];

    public string Id => RuleCatalogueIds.Scope1;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Assigns per-installer user/machine scope to same-URL switch twins and keeps the root scope-free.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { Count: >= 2 } installers)
        {
            return;
        }

        var byUrl = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < installers.Count; i++)
        {
            if (installers[i].InstallerUrl is { } url)
            {
                if (!byUrl.TryGetValue(url, out List<int>? group))
                {
                    group = [];
                    byUrl[url] = group;
                }

                group.Add(i);
            }
        }

        bool assignedTwinScopes = false;
        foreach (List<int> group in byUrl.Values)
        {
            if (group.Count != 2)
            {
                continue;
            }

            assignedTwinScopes |= TryAssignTwinScopes(context, manifest, installers, group[0], group[1]);
        }

        if (assignedTwinScopes && manifest.Scope is { } rootScope)
        {
            // Root scope contradicts the per-installer twins; push it down to entries that
            // still lack one, then clear the root.
            for (int i = 0; i < installers.Count; i++)
            {
                if (installers[i].Scope is null)
                {
                    installers[i].Scope = rootScope;
                    context.AddTrace(this, $"Installers[{i}]: inherited former root Scope '{rootScope}'.");
                }
            }

            manifest.Scope = null;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                "Scope",
                "root scope removed: same-URL user/machine switch twins require per-installer scope",
                RuleChangeConfidence.High);
            context.AddTrace(this, "Removed root Scope: user/machine twins now carry per-installer scope.");
        }
    }

    private bool TryAssignTwinScopes(
        ManifestContext context,
        InstallerManifest manifest,
        List<Installer> installers,
        int firstIndex,
        int secondIndex)
    {
        Scope? first = ClassifySwitches(manifest, installers[firstIndex]);
        Scope? second = ClassifySwitches(manifest, installers[secondIndex]);
        if (first is null || second is null || first == second)
        {
            return false;
        }

        bool changed = false;
        changed |= Assign(context, installers[firstIndex], firstIndex, first.Value);
        changed |= Assign(context, installers[secondIndex], secondIndex, second.Value);
        return changed;
    }

    private bool Assign(ManifestContext context, Installer installer, int index, Scope scope)
    {
        if (installer.Scope is { } existing)
        {
            if (existing != scope)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    $"Installer switches indicate scope '{scope}' but the entry explicitly declares '{existing}'; not changed.",
                    $"Installers[{index}]");
            }

            return false;
        }

        installer.Scope = scope;
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            $"Installers[{index}].Scope",
            $"user/machine switch twin token in installer switches",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"Installers[{index}]: assigned Scope '{scope}' from switch tokens.");
        return true;
    }

    /// <summary>The scope the entry's switches unambiguously indicate, or null.</summary>
    private static Scope? ClassifySwitches(InstallerManifest manifest, Installer installer)
    {
        // Root switches are shared by all entries and cannot distinguish twins.
        InstallerSwitches? switches = installer.InstallerSwitches;
        if (switches is null)
        {
            return null;
        }

        bool user = false;
        bool machine = false;
        foreach (string? value in EnumerateSwitchValues(switches))
        {
            if (value is null)
            {
                continue;
            }

            user |= _userTokens.Any(t => ContainsToken(value, t));
            machine |= _machineTokens.Any(t => ContainsToken(value, t));
        }

        if (user == machine)
        {
            return null;
        }

        return user ? Scope.User : Scope.Machine;
    }

    /// <summary>
    /// Token matching requires a boundary on both sides: "ALLUSERS=1" must not match inside
    /// "ALLUSERS=12", "/CURRENTUSER" not inside "/CURRENTUSERPROFILE", and
    /// "MSIINSTALLPERUSER=1" not inside "MSIINSTALLPERUSER=10".
    /// </summary>
    private static bool ContainsToken(string value, string token)
    {
        int start = 0;
        while (true)
        {
            int index = value.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            bool leftOk = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            int end = index + token.Length;
            bool rightOk = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (leftOk && rightOk)
            {
                return true;
            }

            start = index + 1;
        }
    }

    private static IEnumerable<string?> EnumerateSwitchValues(InstallerSwitches switches)
    {
        yield return switches.Silent;
        yield return switches.SilentWithProgress;
        yield return switches.Interactive;
        yield return switches.Custom;
        yield return switches.Upgrade;
    }
}
