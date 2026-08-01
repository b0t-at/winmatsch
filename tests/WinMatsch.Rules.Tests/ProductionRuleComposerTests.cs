using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class ProductionRuleComposerTests
{
    private static readonly string[] _expectedIds =
    [
        RuleIds.PreserveOnUpdate,
        RuleIds.ApplyPackageQuirks,
        RuleIds.PushDownRootFields,
        RuleIds.ScrubEmptyStrings,
        RuleIds.NormalizeProductCodes,
        RuleIds.DedupeArpVsDefaultLocale,
        RuleIds.RemoveDuplicateInstallers,
        RuleIds.HoistCommonInstallerFields,
        RuleCatalogueIds.Arp1,
        RuleCatalogueIds.Arp2,
        RuleCatalogueIds.Arp3,
        RuleCatalogueIds.Scope1,
        RuleCatalogueIds.Scope2,
        RuleCatalogueIds.Scope3,
        RuleCatalogueIds.Scope4,
        RuleCatalogueIds.Meta5,
        RuleCatalogueIds.Meta1,
        RuleCatalogueIds.Meta3,
        RuleCatalogueIds.Meta4Bullets,
        RuleCatalogueIds.Meta4,
        RuleCatalogueIds.Dep1,
        RuleCatalogueIds.Pipe2,
        RuleCatalogueIds.Pipe4,
        RuleCatalogueIds.Pipe5,
        RuleCatalogueIds.Arp4,
        RuleCatalogueIds.Dep2,
        RuleCatalogueIds.Pipe3,
        RuleCatalogueIds.Pipe1,
        RuleIds.DisplayVersionConsistency,
        RuleIds.DuplicateInstallerEntries,
        RuleIds.InstallerTypeConsistency,
    ];

    [Fact]
    public void Production_catalogue_has_stable_unique_order()
    {
        IReadOnlyList<IRule> rules = ProductionRuleComposer.Compose(
            PolicyEvidence.Empty,
            OverridePackSet.Empty);

        Assert.Equal(_expectedIds, rules.Select(static rule => rule.Id));
        Assert.Equal(rules.Count, rules.Select(static rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        _ = RulePipeline.Create(rules, new RuleRuntimeConfiguration(), OverridePackSet.Empty);
    }

    [Fact]
    public void Production_catalogue_obeys_required_relative_order()
    {
        IReadOnlyList<string> ids =
        [
            .. ProductionRuleComposer.Compose(PolicyEvidence.Empty, OverridePackSet.Empty)
                .Select(static rule => rule.Id),
        ];

        Assert.True(Index(ids, RuleIds.PreserveOnUpdate) < Index(ids, RuleIds.ApplyPackageQuirks));
        Assert.True(Index(ids, RuleCatalogueIds.Arp1) < Index(ids, RuleCatalogueIds.Arp2));
        Assert.True(Index(ids, RuleIds.PreserveOnUpdate) < Index(ids, RuleCatalogueIds.Meta5));
        Assert.True(Index(ids, RuleCatalogueIds.Meta5) < Index(ids, RuleCatalogueIds.Meta1));
        Assert.True(Index(ids, RuleCatalogueIds.Meta5) < Index(ids, RuleCatalogueIds.Meta3));
        Assert.Equal(Index(ids, RuleCatalogueIds.Pipe1), ids.Count - 4);
        Assert.All(ids.Skip(ids.Count - 3), static id => Assert.StartsWith("WM01", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Mutating_policy_rules_are_rejected_after_validation()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            RulePipeline.Create(
                [new DisplayVersionConsistencyRule(), new Arp1VersionTemplateRule()],
                new RuleRuntimeConfiguration()));

        Assert.Contains("mutates manifests", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_modes_are_resolved_without_changing_registry_order()
    {
        var runtime = new RuleRuntimeConfiguration(
            userOverrides: new Dictionary<string, RuleMode>
            {
                [RuleCatalogueIds.Meta1] = RuleMode.LogOnly,
            },
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleCatalogueIds.Meta1] = RuleMode.Disabled,
                [RuleCatalogueIds.Dep2] = RuleMode.LogOnly,
            });
        RulePipeline pipeline = RulePipeline.Create(
            ProductionRuleComposer.Compose(PolicyEvidence.Empty, OverridePackSet.Empty),
            runtime,
            OverridePackSet.Empty);
        ManifestContext context = new()
        {
            Manifests = TestManifests.Create(),
        };

        pipeline.Run(context);

        Assert.Equal(_expectedIds, context.Executions.Select(static execution => execution.RuleId));
        RuleExecution meta = Assert.Single(context.Executions, execution => execution.RuleId == RuleCatalogueIds.Meta1);
        Assert.Equal(RuleMode.Disabled, meta.Mode);
        Assert.Equal(RuleModeSource.CommandOverride, meta.ModeSource);
        RuleExecution dep = Assert.Single(context.Executions, execution => execution.RuleId == RuleCatalogueIds.Dep2);
        Assert.Equal(RuleMode.LogOnly, dep.Mode);
    }

    [Fact]
    public void Meta4_bullet_sub_mode_is_independently_configurable()
    {
        var runtime = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleCatalogueIds.Meta4Bullets] = RuleMode.Disabled,
            });
        RulePipeline pipeline = RulePipeline.Create(
            ProductionRuleComposer.Compose(PolicyEvidence.Empty, OverridePackSet.Empty),
            runtime,
            OverridePackSet.Empty);
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.ReleaseNotes = "- Fixed\nBreaking: config changed";
        ManifestContext context = TestManifests.CreateContext(manifests);

        pipeline.Run(context);

        Assert.Equal("- Fixed\nBreaking\uFF1Aconfig changed", manifests.DefaultLocale.ReleaseNotes);
        Assert.Contains(context.Executions, execution =>
            execution.RuleId == RuleCatalogueIds.Meta4Bullets
            && execution.Mode == RuleMode.Disabled);
    }

    private static int Index(IReadOnlyList<string> ids, string id)
    {
        for (int index = 0; index < ids.Count; index++)
        {
            if (string.Equals(ids[index], id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
