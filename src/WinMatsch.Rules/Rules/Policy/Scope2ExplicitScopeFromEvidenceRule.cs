using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// SCOPE-2: sets an explicit per-installer <c>Scope</c> only from trusted, unambiguous
/// evidence read directly from the installer itself (MSI <c>ALLUSERS</c>, Inno
/// <c>PrivilegesRequired</c>) and supplied through <see cref="PolicyEvidence.InstallerScopes"/>.
/// Evidence originating from generic wrapper metadata is never used — it is reported and
/// ignored. Existing explicit scopes are never overwritten; a conflict produces a finding.
/// </summary>
public sealed class Scope2ExplicitScopeFromEvidenceRule : IRule
{
    private readonly PolicyEvidence _evidence;

    public Scope2ExplicitScopeFromEvidenceRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Scope2;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Sets explicit installer scope from trusted installer-metadata evidence only.";

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
            PolicyScopeEvidence? scopeEvidence = _evidence.FindScopeEvidence(installer.InstallerUrl);
            if (scopeEvidence is null)
            {
                continue;
            }

            if (scopeEvidence.Origin == PolicyScopeEvidenceOrigin.WrapperMetadata)
            {
                context.AddFinding(this, RuleSeverity.Info,
                    $"Scope evidence '{scopeEvidence.Source}' comes from generic wrapper metadata and is not trusted; scope not set.",
                    $"Installers[{i}]");
                continue;
            }

            Scope? effective = EffectiveInstallerValues.GetScope(manifest, installer);
            if (effective is { } existing)
            {
                if (existing != scopeEvidence.Scope)
                {
                    context.AddFinding(this, RuleSeverity.Warning,
                        $"Trusted evidence '{scopeEvidence.Source}' indicates scope '{scopeEvidence.Scope}' but the manifest declares '{existing}'; review required — not changed.",
                        $"Installers[{i}]");
                }

                continue;
            }

            installer.Scope = scopeEvidence.Scope;
            context.AddChangeEvidence(
                this,
                ManifestContext.GetInstallerManifestPath(context.Manifests),
                $"Installers[{i}].Scope",
                scopeEvidence.Source,
                RuleChangeConfidence.High);
            context.AddTrace(this, $"Installers[{i}]: set Scope '{scopeEvidence.Scope}' from trusted evidence ({scopeEvidence.Source}).");
        }
    }
}
