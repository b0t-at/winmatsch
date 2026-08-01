using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// PIPE-4: sets <c>ArchiveBinariesDependOnPath: true</c> on zip-portable installer entries —
/// but only when supplied sibling-import evidence
/// (<see cref="PolicyEvidence.SiblingImportUrls"/>) proved that the portable executable
/// imports DLLs sitting next to it inside the archive. Without that evidence nothing is set,
/// and an already-declared value is never cleared.
/// </summary>
public sealed class Pipe4ArchiveBinariesDependOnPathRule : IRule
{
    private readonly PolicyEvidence _evidence;

    public Pipe4ArchiveBinariesDependOnPathRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Pipe4;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Sets ArchiveBinariesDependOnPath from supplied sibling-import evidence only.";

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
            if (installer.InstallerUrl is not { } url || !_evidence.HasSiblingImports(url))
            {
                continue;
            }

            bool isPortableArchive = EffectiveInstallerValues.GetInstallerType(manifest, installer) == InstallerType.Zip
                && EffectiveInstallerValues.GetNestedInstallerType(manifest, installer) == InstallerType.Portable;
            if (!isPortableArchive)
            {
                context.AddFinding(this, RuleSeverity.Info,
                    "Sibling-import evidence was supplied but the entry is not a zip-portable installer; ArchiveBinariesDependOnPath was not set.",
                    $"Installers[{i}]");
                continue;
            }

            bool? effective = installer.ArchiveBinariesDependOnPath ?? manifest.ArchiveBinariesDependOnPath;
            if (effective == true)
            {
                continue;
            }

            if (effective == false)
            {
                // An explicit false is a human decision; report the contradiction, never flip it.
                context.AddFinding(this, RuleSeverity.Warning,
                    "Sibling-import evidence indicates the portable binaries load DLLs from the archive, but ArchiveBinariesDependOnPath is explicitly false; review required — not changed.",
                    $"Installers[{i}]");
                continue;
            }

            installer.ArchiveBinariesDependOnPath = true;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"Installers[{i}].ArchiveBinariesDependOnPath",
                "archive import analysis: the portable executable loads DLLs shipped next to it in the archive",
                RuleChangeConfidence.High);
            context.AddTrace(this, $"Installers[{i}]: set ArchiveBinariesDependOnPath from sibling-import evidence.");
        }
    }
}
