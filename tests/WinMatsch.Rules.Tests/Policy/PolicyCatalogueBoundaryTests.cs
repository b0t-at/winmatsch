using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

/// <summary>
/// Documents which parts of the external rule catalogue (<c>rules_to_implement.md</c>) are
/// implemented as deterministic, network-free pipeline rules in WinMatsch.Rules — and, just as
/// deliberately, which are NOT, because they require live I/O or repository/PR state and are
/// owned by WinMatsch.Validation and WinMatsch.Workflows instead.
/// </summary>
public class PolicyCatalogueBoundaryTests
{
    /// <summary>The catalogue ids implemented as deterministic policy rules in this assembly.</summary>
    private static readonly Dictionary<string, IRule> _implemented =
        PolicyTestSupport.CreateAllPolicyRules().ToDictionary(static r => r.Id, StringComparer.Ordinal);

    /// <summary>
    /// Catalogue ids deliberately NOT implemented here. Live network probes (META-2 HEAD
    /// checks, HASH-1/2 re-hashing and vanity-URL probing), PR lifecycle management
    /// (WORK-1..5), release-asset enumeration and version derivation (VER-1, MAP-1..4,
    /// ARCH-1..5) and repository-state gates (DUP-1/2 rely on release mapping) need I/O and
    /// belong to WinMatsch.Validation / WinMatsch.Workflows. The policy rules only consume the
    /// evidence those layers supply.
    /// </summary>
    private static readonly string[] _ownedElsewhere =
    [
        RuleCatalogueIds.Arch1, RuleCatalogueIds.Arch2, RuleCatalogueIds.Arch3,
        RuleCatalogueIds.Arch4, RuleCatalogueIds.Arch5,
        RuleCatalogueIds.Map1, RuleCatalogueIds.Map2, RuleCatalogueIds.Map3, RuleCatalogueIds.Map4,
        RuleCatalogueIds.Dup1, RuleCatalogueIds.Dup2,
        RuleCatalogueIds.Hash1, RuleCatalogueIds.Hash2,
        RuleCatalogueIds.Ver1,
        RuleCatalogueIds.Meta2,
        RuleCatalogueIds.Work1, RuleCatalogueIds.Work2, RuleCatalogueIds.Work3,
        RuleCatalogueIds.Work4, RuleCatalogueIds.Work5,
    ];

    public static TheoryData<string> ImplementedIds() => new()
    {
        RuleCatalogueIds.Arp1, RuleCatalogueIds.Arp2, RuleCatalogueIds.Arp3, RuleCatalogueIds.Arp4,
        RuleCatalogueIds.Scope1, RuleCatalogueIds.Scope2, RuleCatalogueIds.Scope3, RuleCatalogueIds.Scope4,
        RuleCatalogueIds.Meta1, RuleCatalogueIds.Meta3, RuleCatalogueIds.Meta4, RuleCatalogueIds.Meta5,
        RuleCatalogueIds.Dep1, RuleCatalogueIds.Dep2,
        RuleCatalogueIds.Pipe1, RuleCatalogueIds.Pipe2, RuleCatalogueIds.Pipe3,
        RuleCatalogueIds.Pipe4, RuleCatalogueIds.Pipe5,
    };

    [Theory]
    [MemberData(nameof(ImplementedIds))]
    public void Every_deterministic_catalogue_id_has_exactly_one_policy_rule(string catalogueId)
    {
        Assert.True(_implemented.ContainsKey(catalogueId),
            $"Catalogue id '{catalogueId}' should be implemented as a policy rule.");
        Assert.Equal(RuleCategory.Policy, _implemented[catalogueId].Category);
        Assert.False(string.IsNullOrWhiteSpace(_implemented[catalogueId].Description));
    }

    [Fact]
    public void Live_and_workflow_catalogue_ids_are_not_duplicated_here()
    {
        foreach (string id in _ownedElsewhere)
        {
            Assert.False(_implemented.ContainsKey(id),
                $"Catalogue id '{id}' is owned by Validation/Workflows and must not be duplicated as a Rules policy rule.");
        }
    }

    [Fact]
    public void Policy_rule_ids_are_unique_and_use_exact_catalogue_spelling()
    {
        IReadOnlyList<IRule> rules = PolicyTestSupport.CreateAllPolicyRules();
        Assert.Equal(rules.Count, rules.Select(static r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, static rule =>
        {
            if (rule.Id != RuleCatalogueIds.Meta4Bullets)
            {
                Assert.Matches("^(ARP|SCOPE|META|DEP|PIPE)-[0-9]$", rule.Id);
            }
        });
    }

    [Fact]
    public void All_policy_rules_run_together_in_one_pipeline()
    {
        // Mutating policy rules first, finding-only rules last (see PolicyTestSupport order);
        // the pipeline accepts the full set without duplicate-id or ordering violations.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var context = new ManifestContext
        {
            Manifests = manifests,
            Options = new RuleOptions { Explain = true },
        };

        var pipeline = new RulePipeline(PolicyTestSupport.CreateAllPolicyRules());
        pipeline.Run(context);

        Assert.Equal(PolicyTestSupport.CreateAllPolicyRules().Count, context.Executions.Count);
        Assert.All(context.Executions, static e => Assert.Equal(RuleMode.Apply, e.Mode));
    }

    [Fact]
    public void Every_policy_rule_supports_all_three_runtime_modes()
    {
        foreach (IRule rule in PolicyTestSupport.CreateAllPolicyRules())
        {
            foreach (RuleMode mode in new[] { RuleMode.Apply, RuleMode.LogOnly, RuleMode.Disabled })
            {
                PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
                ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, mode);
                RuleExecution execution = Assert.Single(context.Executions);
                Assert.Equal(mode, execution.Mode);
            }
        }
    }
}
