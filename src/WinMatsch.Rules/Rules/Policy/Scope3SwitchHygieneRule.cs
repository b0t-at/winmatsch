using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// SCOPE-3: switch hygiene. Trims surrounding whitespace from every installer switch and drops
/// values that are blank after trimming (the wire <c>SilentWithProgress: ' '</c> class of bug);
/// a switches mapping that becomes entirely empty is removed. Additionally, when the installer
/// family (effective InstallerType) changed for a matching entry since the previous version but
/// the switches were carried over verbatim, the carry is flagged for re-detection instead of
/// being silently trusted (the Fork <c>/s</c> → <c>--silent</c> class of bug).
/// </summary>
public sealed class Scope3SwitchHygieneRule : IRule
{
    public string Id => RuleCatalogueIds.Scope3;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Trims and drops blank installer switches; flags switches carried across installer-family changes.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.InstallerSwitches is { } rootSwitches)
        {
            CleanSwitches(context, rootSwitches, "root", string.Empty);
            if (rootSwitches.IsEmpty)
            {
                manifest.InstallerSwitches = null;
                context.AddTrace(this, "root: removed empty InstallerSwitches mapping.");
            }
        }

        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            if (installer.InstallerSwitches is { } switches)
            {
                CleanSwitches(context, switches, $"Installers[{i}]", $"Installers[{i}].");
                if (switches.IsEmpty)
                {
                    installer.InstallerSwitches = null;
                    context.AddTrace(this, $"Installers[{i}]: removed empty InstallerSwitches mapping.");
                }
            }

            FlagFamilyChangeCarry(context, manifest, installer, i);
        }
    }

    private void CleanSwitches(ManifestContext context, InstallerSwitches switches, string location, string fieldPrefix)
    {
        switches.Silent = Clean(context, switches.Silent, location, fieldPrefix, nameof(switches.Silent));
        switches.SilentWithProgress = Clean(context, switches.SilentWithProgress, location, fieldPrefix, nameof(switches.SilentWithProgress));
        switches.Interactive = Clean(context, switches.Interactive, location, fieldPrefix, nameof(switches.Interactive));
        switches.InstallLocation = Clean(context, switches.InstallLocation, location, fieldPrefix, nameof(switches.InstallLocation));
        switches.Log = Clean(context, switches.Log, location, fieldPrefix, nameof(switches.Log));
        switches.Upgrade = Clean(context, switches.Upgrade, location, fieldPrefix, nameof(switches.Upgrade));
        switches.Custom = Clean(context, switches.Custom, location, fieldPrefix, nameof(switches.Custom));
        switches.Repair = Clean(context, switches.Repair, location, fieldPrefix, nameof(switches.Repair));
    }

    private string? Clean(ManifestContext context, string? value, string location, string fieldPrefix, string switchName)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"{fieldPrefix}InstallerSwitches.{switchName}",
                "blank/whitespace-only switch value dropped",
                RuleChangeConfidence.High);
            context.AddTrace(this, $"{location}: dropped blank InstallerSwitches.{switchName}.");
            return null;
        }

        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"{fieldPrefix}InstallerSwitches.{switchName}",
                "surrounding whitespace trimmed from switch value",
                RuleChangeConfidence.High);
            context.AddTrace(this, $"{location}: trimmed InstallerSwitches.{switchName}.");
        }

        return trimmed;
    }

    private void FlagFamilyChangeCarry(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        int index)
    {
        if (context.Previous is not { } previous
            || previous.Installer.Installers is not { } previousInstallers)
        {
            return;
        }

        InstallerType? currentType = EffectiveInstallerValues.GetInstallerType(manifest, installer);
        InstallerSwitches? currentSwitches = EffectiveInstallerValues.GetInstallerSwitches(manifest, installer);
        if (currentType is null || currentSwitches is null)
        {
            return;
        }

        // The entry key embeds the type, so a family change means no same-key match exists.
        // Match by Architecture+Scope instead and compare the types.
        Installer? match = null;
        foreach (Installer candidate in previousInstallers)
        {
            bool sameIdentity = candidate.Architecture == installer.Architecture
                && EffectiveInstallerValues.GetScope(previous.Installer, candidate)
                    == EffectiveInstallerValues.GetScope(manifest, installer);
            if (!sameIdentity)
            {
                continue;
            }

            if (match is not null)
            {
                return;
            }

            match = candidate;
        }

        if (match is null)
        {
            return;
        }

        InstallerType? previousType = EffectiveInstallerValues.GetInstallerType(previous.Installer, match);
        if (previousType is null || previousType == currentType)
        {
            return;
        }

        InstallerSwitches? previousSwitches = EffectiveInstallerValues.GetInstallerSwitches(previous.Installer, match);
        if (previousSwitches is not null && ManifestValues.SwitchesEqual(currentSwitches, previousSwitches))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"InstallerType changed from '{previousType}' to '{currentType}' but the InstallerSwitches were carried over verbatim; re-detect or verify the switches for the new installer family.",
                $"Installers[{index}]");
        }
    }
}
