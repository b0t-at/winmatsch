using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// SCOPE-5: licence-acceptance switches require agreements. winget-pkgs moderators reject a
/// manifest whose installer switches accept a licence or EULA on the user's behalf
/// (<c>accept_eula=1</c>, <c>/ACCEPTEULA</c>, <c>--accept-license</c>, …) unless the locale
/// manifest declares the corresponding <c>Agreements</c>, because winget has to show the user
/// what is being accepted for them (DiRoots.ProSheets, winget-pkgs PR #420154). Such switches
/// are usually carried forward from a previous version, so the rule is finding-only: it never
/// invents an agreement and never drops a switch.
/// </summary>
public sealed partial class Scope5LicenseAcceptanceAgreementRule : IRule
{
    public string Id => RuleCatalogueIds.Scope5;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Flags installer switches that accept a licence or EULA when the locale manifest declares no Agreements.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HasAgreements(context.Manifests))
        {
            return;
        }

        InstallerManifest manifest = context.Manifests.Installer;
        Inspect(context, manifest.InstallerSwitches, "root", string.Empty);
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Inspect(context, installers[i].InstallerSwitches, $"Installers[{i}]", $"Installers[{i}].");
        }
    }

    private static bool HasAgreements(PackageManifests manifests)
        => manifests.DefaultLocale.Agreements is { Count: > 0 } agreements
            && agreements.Any(static agreement =>
                !string.IsNullOrWhiteSpace(agreement.AgreementLabel)
                || !string.IsNullOrWhiteSpace(agreement.Agreement)
                || !string.IsNullOrWhiteSpace(agreement.AgreementUrl));

    private void Inspect(ManifestContext context, InstallerSwitches? switches, string location, string fieldPrefix)
    {
        if (switches is null)
        {
            return;
        }

        Check(context, switches.Silent, location, fieldPrefix, nameof(switches.Silent));
        Check(context, switches.SilentWithProgress, location, fieldPrefix, nameof(switches.SilentWithProgress));
        Check(context, switches.Interactive, location, fieldPrefix, nameof(switches.Interactive));
        Check(context, switches.Upgrade, location, fieldPrefix, nameof(switches.Upgrade));
        Check(context, switches.Custom, location, fieldPrefix, nameof(switches.Custom));
        Check(context, switches.Repair, location, fieldPrefix, nameof(switches.Repair));
    }

    private void Check(ManifestContext context, string? value, string location, string fieldPrefix, string switchName)
    {
        if (value is null)
        {
            return;
        }

        Match match = LicenseAcceptance().Match(value);
        if (!match.Success)
        {
            return;
        }

        context.AddFinding(this, RuleSeverity.Warning,
            $"InstallerSwitches.{switchName} accepts a licence on the user's behalf ('{match.Value}') but the locale manifest declares no Agreements; winget-pkgs requires an Agreements entry (AgreementLabel and AgreementUrl) for switches that accept a licence or EULA.",
            $"{fieldPrefix}InstallerSwitches.{switchName}");
        context.AddTrace(this, $"{location}: InstallerSwitches.{switchName} accepts a licence without Agreements.");
    }

    [GeneratedRegex(
        @"(?:accept|agree)[_\-]?(?:to[_\-]?)?(?:the[_\-]?)?(?:eula|licen[cs]e|terms|agreement)|(?:eula|licen[cs]e|terms)[_\-]?(?:accept(?:ed)?|agree(?:d|ment)?)|\bi[_\-]?(?:accept|agree)(?:licen[cs]e|eula|terms)?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex LicenseAcceptance();
}
