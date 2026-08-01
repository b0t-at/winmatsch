using WinMatsch.Analysis;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// SCOPE-4: the outer wrapper's classification wins over inner-MSI metadata. When installer
/// analysis positively identified the file as a wrapper technology (Burn, Squirrel, NSIS, Inno,
/// Advanced Installer) but the manifest entry claims <c>msi</c>/<c>wix</c> — a classic symptom
/// of trusting the metadata of an MSI embedded inside the wrapper — the entry's InstallerType is
/// corrected to the outer classification and the inner-MSI-derived ProductCode is dropped with a
/// finding. Only typed analyzer evidence triggers the correction; without evidence nothing moves.
/// </summary>
public sealed class Scope4WrapperClassificationRule : IRule
{
    public string Id => RuleCatalogueIds.Scope4;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Corrects msi/wix entries to the analyzer's outer wrapper classification.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        if (manifest.Installers is not { } installers)
        {
            return;
        }

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            InstallerType? declared = EffectiveInstallerValues.GetInstallerType(manifest, installer);
            if (declared is not (InstallerType.Msi or InstallerType.Wix))
            {
                continue;
            }

            DetectedInstallerFormat? format = context.FindEvidence(installer.InstallerUrl)?.Analysis?.Format;
            if (format is null || !TryMapWrapperFormat(format.Value, out InstallerType outerType))
            {
                continue;
            }

            installer.InstallerType = outerType;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"Installers[{i}].InstallerType",
                $"installer analysis classified the outer container as {format}",
                RuleChangeConfidence.High);
            context.AddTrace(this,
                $"Installers[{i}]: corrected InstallerType '{declared}' to '{outerType}' (outer container is {format}).");

            if (installer.ProductCode is { } productCode)
            {
                installer.ProductCode = null;
                context.AddChangeEvidence(
                    this,
                    ManifestContext.GetInstallerManifestPath(context.Manifests),
                    $"Installers[{i}].ProductCode",
                    $"ProductCode '{productCode}' belongs to the embedded MSI, not the {format} wrapper",
                    RuleChangeConfidence.High);
                context.AddFinding(this, RuleSeverity.Warning,
                    $"Dropped ProductCode '{productCode}': it was read from an MSI embedded inside a {format} wrapper and does not identify the outer installer.",
                    $"Installers[{i}]");
            }

            if (format is DetectedInstallerFormat.Squirrel
                && EffectiveInstallerValues.GetScope(manifest, installer) == Scope.Machine)
            {
                context.AddFinding(this, RuleSeverity.Warning,
                    "Squirrel installers install per-user; the declared machine scope likely comes from embedded-MSI metadata and should be reviewed.",
                    $"Installers[{i}]");
            }
        }
    }

    private static bool TryMapWrapperFormat(DetectedInstallerFormat format, out InstallerType installerType)
    {
        switch (format)
        {
            case DetectedInstallerFormat.Burn:
                installerType = InstallerType.Burn;
                return true;
            case DetectedInstallerFormat.Nullsoft:
                installerType = InstallerType.Nullsoft;
                return true;
            case DetectedInstallerFormat.InnoSetup:
                installerType = InstallerType.Inno;
                return true;
            case DetectedInstallerFormat.Squirrel:
            case DetectedInstallerFormat.AdvancedInstaller:
                installerType = InstallerType.Exe;
                return true;
            default:
                // Msi/Msix/Zip/GenericInstallerExe/PortableExe are not wrapper reclassifications.
                installerType = default;
                return false;
        }
    }
}
