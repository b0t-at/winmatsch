using WinMatsch.Core;
using WinMatsch.Core.Yaml;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// PIPE-1: canonical serializer-invariant regression guard. Serializes every manifest through
/// the owning <c>ManifestYamlWriter</c> and asserts the byte-level invariants the repository
/// relies on: LF-only line endings (no CR anywhere) and exactly one trailing newline. Core
/// already emits LF; this rule exists so a CRLF/mixed-endings regression (the "fix line
/// endings" fix-commit class) surfaces as a pipeline finding instead of a rejected PR.
/// Field values that smuggle a raw CR into the output are reported with their document name.
/// Findings only — serialization problems are never "fixed" here.
/// </summary>
public sealed class Pipe1SerializerInvariantsRule : IRule
{
    public string Id => RuleCatalogueIds.Pipe1;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Error;

    public string Description => "Asserts LF-only, single-trailing-newline serializer output for every manifest.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PackageManifests manifests = context.Manifests;
        Check(context, "Version", () => ManifestYamlWriter.Serialize(manifests.Version));
        Check(context, "Installer", () => ManifestYamlWriter.Serialize(manifests.Installer));
        Check(context, "DefaultLocale", () => ManifestYamlWriter.Serialize(manifests.DefaultLocale));
        for (int i = 0; i < manifests.Locales.Count; i++)
        {
            LocaleManifest locale = manifests.Locales[i];
            Check(context, $"Locales[{i}]", () => ManifestYamlWriter.Serialize(locale));
        }
    }

    private void Check(ManifestContext context, string documentName, Func<string> serialize)
    {
        string output;
        try
        {
            output = serialize();
        }
        catch (InvalidOperationException)
        {
            // An unserializable manifest is a schema problem owned by validation, not a
            // line-ending regression; stay silent rather than double-reporting.
            return;
        }

        if (output.Contains('\r', StringComparison.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Error,
                "Serialized manifest contains a carriage return; the canonical serializer must emit LF-only line endings.",
                documentName);
        }

        if (!output.EndsWith('\n'))
        {
            context.AddFinding(this, RuleSeverity.Error,
                "Serialized manifest does not end with a newline; the canonical form requires exactly one trailing newline.",
                documentName);
        }
        else if (output.EndsWith("\n\n", StringComparison.Ordinal))
        {
            context.AddFinding(this, RuleSeverity.Error,
                "Serialized manifest ends with multiple newlines; the canonical form requires exactly one trailing newline.",
                documentName);
        }
    }
}
