namespace WinMatsch.Rules;

/// <summary>
/// A single rule in the pipeline. Normalization and quirk rules mutate the manifests in
/// <see cref="ManifestContext.Manifests"/>; validation rules add findings via
/// <see cref="ManifestContext.AddFinding(IRule, string, string?)"/>. Every mutation and finding
/// should also be traced via <see cref="ManifestContext.AddTrace"/> so <c>--explain</c> can
/// show what each rule did. Rules are stateless and safe to reuse across runs.
/// </summary>
public interface IRule
{
    /// <summary>The stable rule identifier, e.g. <c>WM0001</c> (see <see cref="RuleIds"/>).</summary>
    public string Id { get; }

    public RuleCategory Category { get; }

    /// <summary>The default severity of findings this rule emits; <see cref="RuleSeverity.Info"/> for rules that mutate instead.</summary>
    public RuleSeverity Severity { get; }

    /// <summary>A one-line human-readable description of what the rule does.</summary>
    public string Description { get; }

    /// <summary>Applies the rule to the given context.</summary>
    public void Apply(ManifestContext context);
}
