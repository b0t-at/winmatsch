namespace WinMatsch.Rules;

using System.Collections.ObjectModel;
using WinMatsch.Core;
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

            bool mutates = rule.Category != RuleCategory.Validation;
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
    /// Creates the production pipeline with empty policy evidence and layered built-in package
    /// behavior. This compatibility factory delegates to <see cref="ProductionRuleComposer"/>
    /// so there is one authoritative built-in order.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static RulePipeline CreateDefault(IEnumerable<string>? disabledRuleIds = null) => new(
        ProductionRuleComposer.Compose(overridePacks: OverridePackSet.BuiltIn),
        disabledRuleIds);

    /// <summary>Creates the production pipeline with layered runtime and package overrides.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static RulePipeline CreateDefaultWithRuntime(
        RuleRuntimeConfiguration runtimeConfiguration,
        OverridePackSet? overridePacks = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        OverridePackSet packs = overridePacks ?? OverridePackSet.BuiltIn;
        return new(ProductionRuleComposer.Compose(overridePacks: packs), runtimeConfiguration, packs);
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
            AddSnapshotFailure(rule, context, result: false);
            return;
        }

        PackageManifests beforeManifests = ManifestSnapshot.Clone(context.Manifests);
        ManifestContext simulation = CreateSimulation(context);
        rule.Apply(simulation);
        if (!ManifestSnapshot.TryCapture(simulation.Manifests, out ManifestSnapshot after))
        {
            AddSnapshotFailure(rule, context, result: true);
            return;
        }

        ManifestClone.CopyTo(simulation.Manifests, context.Manifests);
        context.ImportFindings(simulation.Findings);
        context.ImportTrace(simulation.Trace);
        RecordChanges(rule, context, before.Diff(after), resolution, simulation, beforeManifests);
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
            AddSnapshotFailure(rule, context, result: false);
            return;
        }
        ManifestContext simulation = CreateSimulation(context);

        rule.Apply(simulation);
        if (!ManifestSnapshot.TryCapture(simulation.Manifests, out ManifestSnapshot after))
        {
            AddSnapshotFailure(rule, context, result: true);
            return;
        }
        context.ImportFindings(simulation.Findings);
        context.ImportTrace(simulation.Trace);
        RecordChanges(
            rule,
            context,
            before.Diff(after),
            resolution,
            simulation,
            context.Manifests);
    }

    private static ManifestContext CreateSimulation(ManifestContext context)
        => new()
        {
            Manifests = ManifestSnapshot.Clone(context.Manifests),
            Previous = context.Previous is null ? null : ManifestSnapshot.Clone(context.Previous),
            OriginalBotSubmission = context.OriginalBotSubmission is null
                ? null
                : ManifestSnapshot.Clone(context.OriginalBotSubmission),
            Evidence = context.Evidence,
            Options = context.Options,
        };

    private static void AddSnapshotFailure(
        IRule rule,
        ManifestContext context,
        bool result)
        => context.AddFinding(
            rule,
            RuleSeverity.Error,
            result
                ? "The rule produced a manifest state that could not be snapshotted safely; no changes were applied."
                : "The rule could not run because the input manifest state could not be snapshotted safely; no changes were applied.",
            "RulePipeline");

    private static void RecordChanges(
        IRule rule,
        ManifestContext target,
        IReadOnlyList<RawManifestChange> changes,
        RuleModeResolution resolution,
        ManifestContext evidenceContext,
        PackageManifests beforeManifests)
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
                ?? ResolveInstallerEvidence(change, evidenceContext, beforeManifests)
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
        ManifestContext context,
        PackageManifests beforeManifests)
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
            || (change.After is null ? beforeManifests : context.Manifests).Installer.Installers is not { } installers
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
