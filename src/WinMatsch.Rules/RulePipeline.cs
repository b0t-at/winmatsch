namespace WinMatsch.Rules;

/// <summary>
/// Runs a hand-written, ordered list of rules against a <see cref="ManifestContext"/>.
/// The order is fixed at construction (no reflection-based discovery) and mutating rules
/// (normalization, quirk) must all come before validation rules, so a run is fully
/// deterministic: same input, same mutations, same findings in the same order.
/// Individual rules can be disabled by id without changing the order of the rest.
/// </summary>
public sealed class RulePipeline
{
    private readonly IReadOnlyList<IRule> _rules;
    private readonly HashSet<string> _disabledRuleIds;

    /// <summary>Creates a pipeline over an explicit rule list.</summary>
    /// <param name="rules">The rules, in execution order. Ids must be unique and mutating rules must precede validation rules.</param>
    /// <param name="disabledRuleIds">Ids of rules to skip (case-insensitive); unknown ids are ignored.</param>
    public RulePipeline(IReadOnlyList<IRule> rules, IEnumerable<string>? disabledRuleIds = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool validationSeen = false;
        foreach (IRule rule in rules)
        {
            if (!ids.Add(rule.Id))
            {
                throw new ArgumentException($"Duplicate rule id '{rule.Id}'.", nameof(rules));
            }

            bool mutates = rule.Category is RuleCategory.Normalization or RuleCategory.Quirk;
            if (mutates && validationSeen)
            {
                throw new ArgumentException($"Rule '{rule.Id}' mutates manifests but is ordered after a validation rule; mutating rules must run first.", nameof(rules));
            }

            validationSeen |= rule.Category == RuleCategory.Validation;
        }

        _rules = [.. rules];
        _disabledRuleIds = disabledRuleIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(disabledRuleIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The rules in execution order, including disabled ones.</summary>
    public IReadOnlyList<IRule> Rules => _rules;

    /// <summary>The ids of rules this pipeline skips.</summary>
    public IReadOnlyCollection<string> DisabledRuleIds => _disabledRuleIds;

    /// <summary>Runs all enabled rules in order and returns the findings collected on the context.</summary>
    public IReadOnlyList<RuleFinding> Run(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (IRule rule in _rules)
        {
            if (_disabledRuleIds.Contains(rule.Id))
            {
                continue;
            }

            rule.Apply(context);
        }

        return context.Findings;
    }

    /// <summary>
    /// Creates the default pipeline. The order is deliberate: previous-version carry-over first
    /// (so later normalization sees the complete data), then quirks, then the generic
    /// normalization passes ending with hoisting, and validation last.
    /// </summary>
    public static RulePipeline CreateDefault(IEnumerable<string>? disabledRuleIds = null) => new(
        [
            new PreserveOnUpdateRule(),
            new ApplyPackageQuirksRule(),
            new PushDownRootFieldsRule(),
            new ScrubEmptyStringsRule(),
            new NormalizeProductCodesRule(),
            new DedupeArpVsDefaultLocaleRule(),
            new RemoveDuplicateInstallersRule(),
            new HoistCommonInstallerFieldsRule(),
            new DisplayVersionConsistencyRule(),
            new DuplicateInstallerEntriesRule(),
            new InstallerTypeConsistencyRule(),
        ],
        disabledRuleIds);
}
