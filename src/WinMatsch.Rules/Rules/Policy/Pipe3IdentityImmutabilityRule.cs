using WinMatsch.Analysis;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// PIPE-3: package identity is immutable and case-sensitive. On updates, the PackageIdentifier
/// must equal the previous merged version's identifier byte-for-byte — identity is resolved
/// from the repository, never regenerated from names. All four manifests must agree on
/// identifier and version with exact casing. For MSIX/AppX entries, identity evidence must be
/// complete: <c>PackageFamilyName</c> and <c>SignatureSha256</c> present, the family name
/// matching MSIX analysis evidence when supplied, and no half-filled Publisher-only ARP
/// entries. Findings only — identity is never rewritten by a rule.
/// </summary>
public sealed class Pipe3IdentityImmutabilityRule : IRule
{
    public string Id => RuleCatalogueIds.Pipe3;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Error;

    public string Description => "Enforces exact-casing identity across versions and complete MSIX identity evidence.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CheckPreviousIdentity(context);
        CheckCrossManifestIdentity(context);
        CheckMsixIdentity(context);
    }

    private void CheckPreviousIdentity(ManifestContext context)
    {
        if (context.Previous is not { } previous)
        {
            return;
        }

        string? current = context.Manifests.Installer.PackageIdentifier?.Value;
        string? before = previous.Installer.PackageIdentifier?.Value;
        if (current is null || before is null || string.Equals(current, before, StringComparison.Ordinal))
        {
            return;
        }

        string casingHint = string.Equals(current, before, StringComparison.OrdinalIgnoreCase)
            ? " (the identifiers differ only in casing — identity is case-sensitive and must be resolved from the repository's exact folder casing)"
            : string.Empty;
        context.AddFinding(this, RuleSeverity.Error,
            $"PackageIdentifier '{current}' does not match the previous version's '{before}'{casingHint}. Package identity is immutable; publisher moves need an explicit move PR.");
    }

    private void CheckCrossManifestIdentity(ManifestContext context)
    {
        PackageManifests manifests = context.Manifests;
        string? identifier = manifests.Installer.PackageIdentifier?.Value;
        string? version = manifests.Installer.PackageVersion?.Value;

        CheckDocument(context, "Version", manifests.Version.PackageIdentifier?.Value, manifests.Version.PackageVersion?.Value, identifier, version);
        CheckDocument(context, "DefaultLocale", manifests.DefaultLocale.PackageIdentifier?.Value, manifests.DefaultLocale.PackageVersion?.Value, identifier, version);
        for (int i = 0; i < manifests.Locales.Count; i++)
        {
            LocaleManifest locale = manifests.Locales[i];
            CheckDocument(context, $"Locales[{i}]", locale.PackageIdentifier?.Value, locale.PackageVersion?.Value, identifier, version);
        }
    }

    private void CheckDocument(
        ManifestContext context,
        string documentName,
        string? documentIdentifier,
        string? documentVersion,
        string? identifier,
        string? version)
    {
        if (documentIdentifier is not null && identifier is not null
            && !string.Equals(documentIdentifier, identifier, StringComparison.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Error,
                $"PackageIdentifier '{documentIdentifier}' does not exactly match the installer manifest's '{identifier}'; all manifests must agree with exact casing.",
                documentName);
        }

        if (documentVersion is not null && version is not null
            && !string.Equals(documentVersion, version, StringComparison.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Error,
                $"PackageVersion '{documentVersion}' does not exactly match the installer manifest's '{version}'; all manifests must agree with exact casing.",
                documentName);
        }
    }

    private void CheckMsixIdentity(ManifestContext context)
    {
        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            InstallerType? type = EffectiveInstallerValues.GetInstallerType(manifest, installer);
            if (type is not (InstallerType.Msix or InstallerType.Appx))
            {
                continue;
            }

            string location = $"Installers[{i}]";
            string? familyName = installer.PackageFamilyName ?? manifest.PackageFamilyName;
            if (familyName is null)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "MSIX/AppX installer entry has no PackageFamilyName; derive it from the package identity rather than omitting it.",
                    location);
            }

            if (installer.SignatureSha256 is null)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "MSIX/AppX installer entry has no SignatureSha256; the signature hash is part of complete MSIX identity evidence.",
                    location);
            }

            CheckAnalysisFamilyName(context, installer, familyName, location);
            CheckHalfFilledArp(context, manifest, installer, location);
        }
    }

    private void CheckAnalysisFamilyName(
        ManifestContext context,
        Installer installer,
        string? familyName,
        string location)
    {
        InstallerAnalysis? analysis = context.FindEvidence(installer.InstallerUrl)?.Analysis;
        if (analysis is null || familyName is null)
        {
            return;
        }

        string? analyzed = analysis.Installers
            .Select(static i => i.PackageFamilyName)
            .FirstOrDefault(static n => n is not null);
        if (analyzed is not null && !string.Equals(analyzed, familyName, StringComparison.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"PackageFamilyName '{familyName}' does not match the MSIX identity '{analyzed}' read from the package; the signed identity is authoritative.",
                location);
        }
    }

    private void CheckHalfFilledArp(
        ManifestContext context,
        InstallerManifest manifest,
        Installer installer,
        string location)
    {
        List<AppsAndFeaturesEntry>? entries = EffectiveInstallerValues.GetAppsAndFeaturesEntries(manifest, installer);
        if (entries is null)
        {
            return;
        }

        foreach (AppsAndFeaturesEntry entry in entries)
        {
            bool publisherOnly = entry.Publisher is not null
                && entry.DisplayName is null
                && entry.DisplayVersion is null
                && entry.ProductCode is null
                && entry.UpgradeCode is null
                && entry.InstallerType is null;
            if (publisherOnly)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "MSIX installer entry carries a half-filled ARP entry declaring only Publisher; MSIX identity comes from the package, so drop the entry or complete it.",
                    location);
            }
        }
    }
}
