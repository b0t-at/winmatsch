namespace WinMatsch.Rules;

using System.Collections.ObjectModel;
using WinMatsch.Rules.OverridePacks;

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
    private readonly RuleRuntimeConfiguration _runtimeConfiguration;
    private readonly OverridePackSet _overridePacks;
    private readonly IReadOnlyCollection<string> _disabledRuleIds;

    /// <summary>Creates a pipeline over an explicit rule list.</summary>
    /// <param name="rules">The rules, in execution order. Ids must be unique and mutating rules must precede validation rules.</param>
    /// <param name="disabledRuleIds">Ids of rules to skip (case-insensitive); unknown ids are ignored.</param>
    public RulePipeline(IReadOnlyList<IRule> rules, IEnumerable<string>? disabledRuleIds = null)
        : this(rules, RuleRuntimeConfiguration.FromDisabled(disabledRuleIds), OverridePackSet.BuiltIn)
    {
    }

    private RulePipeline(
        IReadOnlyList<IRule> rules,
        RuleRuntimeConfiguration runtimeConfiguration,
        OverridePackSet overridePacks)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);

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
        _runtimeConfiguration = runtimeConfiguration;
        _overridePacks = overridePacks;
        _disabledRuleIds = new ReadOnlyCollection<string>(
            runtimeConfiguration.CommandOverrides
                .Where(static pair => pair.Value == RuleMode.Disabled)
                .Select(static pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    /// <summary>The rules in execution order, including disabled ones.</summary>
    public IReadOnlyList<IRule> Rules => _rules;

    /// <summary>
    /// Rule ids unconditionally disabled by the highest-precedence command layer. Package and
    /// user modes are context-dependent and are reported through <see cref="ManifestContext.Executions"/>.
    /// </summary>
    public IReadOnlyCollection<string> DisabledRuleIds => _disabledRuleIds;

    /// <summary>Creates a pipeline with explicit runtime mode and package override inputs.</summary>
    public static RulePipeline Create(
        IReadOnlyList<IRule> rules,
        RuleRuntimeConfiguration runtimeConfiguration,
        OverridePackSet? overridePacks = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        return new(rules, runtimeConfiguration, overridePacks ?? OverridePackSet.Empty);
    }

    /// <summary>Runs all enabled rules in order and returns the findings collected on the context.</summary>
    public IReadOnlyList<RuleFinding> Run(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _overridePacks.TryGet(context.Manifests.Installer.PackageIdentifier, out OverridePack? packageOverride);

        foreach (IRule rule in _rules)
        {
            RuleModeResolution resolution = _runtimeConfiguration.Resolve(rule.Id, packageOverride?.RuleModes);
            context.AddExecution(new(rule.Id, resolution.Mode, resolution.Source));
            if (resolution.Mode == RuleMode.Disabled)
            {
                continue;
            }

            if (resolution.Mode == RuleMode.LogOnly)
            {
                RunLogOnly(rule, context, resolution);
            }
            else
            {
                RunApplied(rule, context, resolution);
            }
        }

        HumanCorrectionDetector.Detect(context);
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

    /// <summary>Creates the default pipeline with layered runtime and package overrides.</summary>
    public static RulePipeline CreateDefaultWithRuntime(
        RuleRuntimeConfiguration runtimeConfiguration,
        OverridePackSet? overridePacks = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        OverridePackSet packs = overridePacks ?? OverridePackSet.BuiltIn;
        return new(
            [
                new PreserveOnUpdateRule(),
                new ApplyPackageQuirksRule(packs),
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
            runtimeConfiguration,
            packs);
    }

    private static void RunApplied(
        IRule rule,
        ManifestContext context,
        RuleModeResolution resolution)
    {
        if (rule.Category == RuleCategory.Validation)
        {
            rule.Apply(context);
            return;
        }

        if (!ManifestSnapshot.TryCapture(context.Manifests, out ManifestSnapshot before))
        {
            rule.Apply(context);
            return;
        }

        rule.Apply(context);
        if (!ManifestSnapshot.TryCapture(context.Manifests, out ManifestSnapshot after))
        {
            return;
        }

        RecordChanges(rule, context, before.Diff(after), resolution, context);
    }

    private static void RunLogOnly(
        IRule rule,
        ManifestContext context,
        RuleModeResolution resolution)
    {
        if (rule.Category == RuleCategory.Validation)
        {
            rule.Apply(context);
            return;
        }

        if (!ManifestSnapshot.TryCapture(context.Manifests, out ManifestSnapshot before))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' cannot run in log-only mode because the manifest cannot be snapshotted.");
        }
        var simulation = new ManifestContext
        {
            Manifests = ManifestSnapshot.Clone(context.Manifests),
            Previous = context.Previous is null ? null : ManifestSnapshot.Clone(context.Previous),
            OriginalBotSubmission = context.OriginalBotSubmission is null
                ? null
                : ManifestSnapshot.Clone(context.OriginalBotSubmission),
            Evidence = context.Evidence,
            Options = context.Options,
        };

        rule.Apply(simulation);
        if (!ManifestSnapshot.TryCapture(simulation.Manifests, out ManifestSnapshot after))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' log-only result cannot be snapshotted.");
        }
        context.ImportFindings(simulation.Findings);
        context.ImportTrace(simulation.Trace);
        RecordChanges(rule, context, before.Diff(after), resolution, simulation);
    }

    private static void RecordChanges(
        IRule rule,
        ManifestContext target,
        IReadOnlyList<RawManifestChange> changes,
        RuleModeResolution resolution,
        ManifestContext evidenceContext)
    {
        foreach (RawManifestChange change in changes)
        {
            if (change.IsPairing)
            {
                continue;
            }

            RuleChangeEvidence evidence = evidenceContext.FindChangeEvidence(
                    rule.Id,
                    change.ManifestPath,
                    change.FieldPath)
                ?? ResolveInstallerEvidence(change, evidenceContext)
                ?? new($"rule {rule.Id}", RuleChangeConfidence.High);

            target.AddChange(new(
                rule.Id,
                resolution.Mode,
                resolution.Source,
                change.ManifestPath,
                change.FieldPath,
                RuleLogSanitizer.Sanitize(change.FieldPath, change.Before),
                RuleLogSanitizer.Sanitize(change.FieldPath, change.After),
                RuleLogSanitizer.SanitizeMessage(evidence.Source),
                evidence.Confidence));
        }
    }

    private static RuleChangeEvidence? ResolveInstallerEvidence(
        RawManifestChange change,
        ManifestContext context)
    {
        const string prefix = "Installers[";
        if (!change.FieldPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        int close = change.FieldPath.IndexOf(']', prefix.Length);
        if (close < 0
            || !int.TryParse(
                change.FieldPath.AsSpan(prefix.Length, close - prefix.Length),
                out int index)
            || context.Manifests.Installer.Installers is not { } installers
            || index < 0
            || index >= installers.Count)
        {
            return null;
        }

        InstallerEvidence? evidence = context.FindEvidence(installers[index].InstallerUrl);
        if (evidence is null)
        {
            return null;
        }

        RuleChangeConfidence confidence = evidence.Analysis is null
            ? RuleChangeConfidence.Medium
            : RuleChangeConfidence.High;
        return new($"installer analysis for {evidence.InstallerUrl}", confidence);
    }
}
