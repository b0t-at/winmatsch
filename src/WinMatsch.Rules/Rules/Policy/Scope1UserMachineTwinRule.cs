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
            RepairInheritedScopeSwitch(context, manifest, installers, group[0], group[1]);
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

    /// <summary>
    /// Nullsoft user/machine twins that declare opposite explicit scopes but share one root
    /// switch set carrying a single MultiUser token (Automattic.Wordpress: root
    /// <c>Custom: /CURRENTUSER</c> for both twins) install the machine twin per-user. The twin
    /// whose scope contradicts the token gets its own switch set with the paired token
    /// (<c>/CURRENTUSER</c> ↔ <c>/ALLUSERS</c>); nothing else changes.
    /// </summary>
    private void RepairInheritedScopeSwitch(
        ManifestContext context,
        InstallerManifest manifest,
        List<Installer> installers,
        int firstIndex,
        int secondIndex)
    {
        Installer first = installers[firstIndex];
        Installer second = installers[secondIndex];
        if (first.Scope is not { } firstScope
            || second.Scope is not { } secondScope
            || firstScope == secondScope
            || first.InstallerSwitches is not null
            || second.InstallerSwitches is not null
            || manifest.InstallerSwitches is not { } shared
            || (first.InstallerType ?? manifest.InstallerType) != InstallerType.Nullsoft
            || (second.InstallerType ?? manifest.InstallerType) != InstallerType.Nullsoft)
        {
            return;
        }

        string?[] values = [.. EnumerateSwitchValues(shared)];
        bool user = values.Any(static value => value is not null && ContainsToken(value, "/CURRENTUSER"));
        bool machine = values.Any(static value => value is not null && ContainsToken(value, "/ALLUSERS"));
        if (user == machine)
        {
            return;
        }

        Scope tokenScope = user ? Scope.User : Scope.Machine;
        (Installer target, int index) = firstScope == tokenScope ? (second, secondIndex) : (first, firstIndex);
        string from = user ? "/CURRENTUSER" : "/ALLUSERS";
        string to = user ? "/ALLUSERS" : "/CURRENTUSER";
        InstallerSwitches repaired = ManifestValues.CloneSwitches(shared);
        repaired.Silent = ReplaceToken(repaired.Silent, from, to);
        repaired.SilentWithProgress = ReplaceToken(repaired.SilentWithProgress, from, to);
        repaired.Interactive = ReplaceToken(repaired.Interactive, from, to);
        repaired.Custom = ReplaceToken(repaired.Custom, from, to);
        repaired.Upgrade = ReplaceToken(repaired.Upgrade, from, to);
        target.InstallerSwitches = repaired;
        context.AddChangeEvidence(
            this,
            ManifestContext.GetInstallerManifestPath(context.Manifests),
            $"Installers[{index}].InstallerSwitches",
            $"the {target.Scope} twin inherited the shared {from} switch; the paired NSIS MultiUser token {to} selects its declared scope",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"Installers[{index}]: replaced inherited {from} with {to} for its declared scope.");
    }

    private static string? ReplaceToken(string? value, string token, string replacement)
    {
        if (value is null || !ContainsToken(value, token))
        {
            return value;
        }

        int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool leftOk = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            int end = index + token.Length;
            bool rightOk = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (leftOk && rightOk)
            {
                value = string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(end));
                end = index + replacement.Length;
            }

            index = value.IndexOf(token, end, StringComparison.OrdinalIgnoreCase);
        }

        return value;
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
