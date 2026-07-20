using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// WM0002: resolves conflicts between root-level installer fields and per-installer values
/// (the wingetcreate "shift root fields to installer level" concept). Semantics, kept simple
/// on purpose: a root value is the default for installers that leave the field null. When no
/// installer overrides the field it stays at the root; when every override equals the root the
/// redundant per-installer copies are cleared; when any installer carries a different value the
/// field can no longer live at the root, so the root value is deep-cloned into every installer
/// that lacks it and the root field is cleared.
/// </summary>
public sealed class PushDownRootFieldsRule : IRule
{
    public string Id => RuleIds.PushDownRootFields;

    public RuleCategory Category => RuleCategory.Normalization;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Pushes root installer fields down to the installers when a per-installer value conflicts with the root.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InstallerManifest manifest = context.Manifests.Installer;
        List<Installer>? installers = manifest.Installers;
        if (installers is null || installers.Count == 0)
        {
            return;
        }

        foreach (InstallerFieldAccessor accessor in InstallerFieldAccessors.All)
        {
            object? rootValue = accessor.Get(manifest);
            if (rootValue is null)
            {
                continue;
            }

            bool anyOverride = false;
            bool anyConflict = false;
            foreach (Installer installer in installers)
            {
                if (accessor.Get(installer) is { } value)
                {
                    anyOverride = true;
                    anyConflict |= !accessor.ValueEquals(rootValue, value);
                }
            }

            if (!anyOverride)
            {
                continue;
            }

            if (!anyConflict)
            {
                // All overrides equal the root default; the copies are redundant.
                foreach (Installer installer in installers)
                {
                    accessor.Set(installer, null);
                }

                context.AddTrace(this, $"Removed per-installer {accessor.Name} values that duplicate the manifest root value.");
                continue;
            }

            foreach (Installer installer in installers)
            {
                if (accessor.Get(installer) is null)
                {
                    accessor.Set(installer, accessor.Clone(rootValue));
                }
            }

            accessor.Set(manifest, null);
            context.AddTrace(this, $"Pushed root {accessor.Name} down to the installers because a per-installer value conflicts with it.");
        }
    }
}
