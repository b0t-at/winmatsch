using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class RulePipelineTests
{
    private sealed class FakeRule(string id, RuleCategory category) : IRule
    {
        public string Id => id;

        public RuleCategory Category => category;

        public RuleSeverity Severity => RuleSeverity.Info;

        public string Description => "Fake rule for pipeline tests.";

        public void Apply(ManifestContext context)
        {
        }
    }

    [Fact]
    public void Default_pipeline_runs_all_mutating_rules_before_validation_rules()
    {
        RulePipeline pipeline = RulePipeline.CreateDefault();

        int firstValidation = -1;
        for (int i = 0; i < pipeline.Rules.Count; i++)
        {
            if (pipeline.Rules[i].Category == RuleCategory.Validation)
            {
                firstValidation = i;
                break;
            }
        }

        Assert.True(firstValidation > 0);
        for (int i = firstValidation; i < pipeline.Rules.Count; i++)
        {
            Assert.Equal(RuleCategory.Validation, pipeline.Rules[i].Category);
        }
    }

    [Fact]
    public void Default_pipeline_rule_ids_are_unique_and_stable()
    {
        RulePipeline pipeline = RulePipeline.CreateDefault();

        var ids = new List<string>();
        foreach (IRule rule in pipeline.Rules)
        {
            ids.Add(rule.Id);
        }

        Assert.Equal(ids.Count, new HashSet<string>(ids, StringComparer.Ordinal).Count);
        Assert.Equal(
            ProductionRuleComposer.Compose(overridePacks: OverridePackSet.BuiltIn)
                .Select(static rule => rule.Id),
            ids);
    }

    [Fact]
    public void Constructor_rejects_duplicate_rule_ids()
    {
        Assert.Throws<ArgumentException>(() => new RulePipeline([new FakeRule("WM9001", RuleCategory.Normalization), new FakeRule("WM9001", RuleCategory.Validation)]));
    }

    [Fact]
    public void Constructor_rejects_mutating_rules_ordered_after_validation_rules()
    {
        Assert.Throws<ArgumentException>(() => new RulePipeline([new FakeRule("WM9001", RuleCategory.Validation), new FakeRule("WM9002", RuleCategory.Quirk)]));
    }

    [Fact]
    public void Legacy_null_calls_remain_source_compatible_and_unambiguous()
    {
        var explicitPipeline = new RulePipeline([], null);
        RulePipeline defaultPipeline = RulePipeline.CreateDefault(null);

        Assert.Empty(explicitPipeline.Rules);
        Assert.NotEmpty(defaultPipeline.Rules);
    }

    [Fact]
    public void Disabled_rules_are_skipped()
    {
        Installer a = TestManifests.CreateInstaller();
        a.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        PackageManifests manifests = TestManifests.Create(a);
        ManifestContext context = TestManifests.CreateContext(manifests);

        RulePipeline.CreateDefault(disabledRuleIds: [RuleIds.NormalizeProductCodes, RuleIds.HoistCommonInstallerFields]).Run(context);

        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", a.ProductCode);
        Assert.Null(manifests.Installer.InstallerType);
    }

    [Fact]
    public void Explain_produces_a_trace_and_off_by_default_produces_none()
    {
        static (PackageManifests Manifests, Installer Installer) CreateInput()
        {
            Installer installer = TestManifests.CreateInstaller();
            installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
            return (TestManifests.Create(installer), installer);
        }

        ManifestContext explained = TestManifests.CreateContext(CreateInput().Manifests, explain: true);
        ManifestContext silent = TestManifests.CreateContext(CreateInput().Manifests, explain: false);

        RulePipeline.CreateDefault().Run(explained);
        RulePipeline.CreateDefault().Run(silent);

        Assert.NotEmpty(explained.Trace);
        Assert.Contains(explained.Trace, t => t.RuleId == RuleIds.NormalizeProductCodes);
        Assert.Empty(silent.Trace);
    }

    [Fact]
    public void Run_returns_the_findings_collected_on_the_context()
    {
        PackageManifests manifests = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.com/a.msi"),
            TestManifests.CreateInstaller(url: "https://example.com/b.msi"));
        ManifestContext context = TestManifests.CreateContext(manifests);

        IReadOnlyList<RuleFinding> findings = RulePipeline.CreateDefault().Run(context);

        Assert.Same(context.Findings, findings);
        Assert.Contains(findings, f => f.RuleId == RuleIds.DuplicateInstallerEntries);
    }

    [Fact]
    public void Pipeline_runs_are_deterministic()
    {
        static ManifestContext CreateMessyContext()
        {
            Installer a = TestManifests.CreateInstaller(url: "https://example.com/a.msi");
            a.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
            a.AppsAndFeaturesEntries = [new AppsAndFeaturesEntry { DisplayName = TestManifests.DefaultPackageName, DisplayVersion = "1.0" }];
            Installer b = TestManifests.CreateInstaller(url: "https://example.com/b.msi");
            b.Commands = ["", "app"];
            return TestManifests.CreateContext(TestManifests.Create(a, b), explain: true);
        }

        ManifestContext first = CreateMessyContext();
        ManifestContext second = CreateMessyContext();

        RulePipeline.CreateDefault().Run(first);
        RulePipeline.CreateDefault().Run(second);

        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(first.Trace, second.Trace);
    }

    [Fact]
    public void Removed_installer_change_uses_pre_rule_evidence()
    {
        Installer first = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
        Installer duplicate = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
        Installer third = TestManifests.CreateInstaller(url: "https://example.test/c.exe");
        PackageManifests manifests = TestManifests.Create(first, duplicate, third);
        ManifestContext context = TestManifests.CreateContext(
            manifests,
            evidence:
            [
                new InstallerEvidence { InstallerUrl = first.InstallerUrl!, Properties = new Dictionary<string, string>() },
                new InstallerEvidence { InstallerUrl = third.InstallerUrl!, Properties = new Dictionary<string, string>() },
            ]);

        RulePipeline.Create(
            [new RemoveDuplicateInstallersRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.DoesNotContain(
            context.Changes,
            change => change.FieldPath.StartsWith("Installers[1]", StringComparison.Ordinal)
                && change.SourceEvidence.Contains("c.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Structural_commit_preserves_retained_installer_objects()
    {
        Installer first = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
        Installer duplicate = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
        Installer retained = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
        PackageManifests manifests = TestManifests.Create(first, duplicate, retained);
        ManifestContext context = TestManifests.CreateContext(manifests);

        RulePipeline.Create(
            [new RemoveDuplicateInstallersRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Collection(
            manifests.Installer.Installers!,
            installer => Assert.Same(first, installer),
            installer => Assert.Same(retained, installer));
        Assert.DoesNotContain(duplicate, manifests.Installer.Installers!);
    }

    [Theory]
    [InlineData(RuleMode.Apply)]
    [InlineData(RuleMode.LogOnly)]
    public void Unsnapshotable_rule_output_fails_closed_with_mode_parity(RuleMode mode)
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests previous = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests original = TestManifests.Create(TestManifests.CreateInstaller());
        previous.DefaultLocale.ShortDescription = "previous-safe";
        original.DefaultLocale.ShortDescription = "original-safe";
        var runtime = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [UnsnapshotableOutputRule.RuleId] = mode,
            });
        ManifestContext context = new()
        {
            Manifests = manifests,
            Previous = previous,
            OriginalBotSubmission = original,
        };

        RulePipeline.Create(
            [new UnsnapshotableOutputRule()],
            runtime,
            OverridePackSet.Empty).Run(context);

        Assert.Single(manifests.Installer.Installers!);
        Assert.Equal("previous-safe", previous.DefaultLocale.ShortDescription);
        Assert.Equal("original-safe", original.DefaultLocale.ShortDescription);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(UnsnapshotableOutputRule.RuleId, finding.RuleId);
        Assert.Contains("no changes were applied", finding.Message, StringComparison.Ordinal);
        Assert.Empty(context.Changes);
    }

    [Theory]
    [InlineData(RuleMode.Apply)]
    [InlineData(RuleMode.LogOnly)]
    public void Unsnapshotable_rule_input_fails_closed_without_invoking_rule(RuleMode mode)
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.Installer.Installers!.Add(null!);
        var rule = new InvocationTrackingRule();
        var runtime = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [rule.Id] = mode,
            });
        ManifestContext context = TestManifests.CreateContext(manifests);

        RulePipeline.Create([rule], runtime, OverridePackSet.Empty).Run(context);

        Assert.False(rule.WasInvoked);
        Assert.Equal(2, manifests.Installer.Installers.Count);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("input manifest state", finding.Message, StringComparison.Ordinal);
        Assert.Empty(context.Changes);
    }

    [Fact]
    public void Apply_invokes_stateful_rule_once_and_commits_validated_result()
    {
        Installer installer = TestManifests.CreateInstaller();
        PackageManifests manifests = TestManifests.Create(installer);
        var rule = new StatefulRule();
        ManifestContext context = TestManifests.CreateContext(manifests);

        RulePipeline.Create(
            [rule],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Equal(1, rule.InvocationCount);
        Assert.Equal("RUN-1", installer.ProductCode);
        Assert.Contains(context.Changes, change =>
            change.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
            && change.After == "RUN-1");
    }

    [Fact]
    public void Full_pipeline_normalizes_a_messy_manifest_end_to_end()
    {
        Installer a = TestManifests.CreateInstaller(url: "https://example.com/app-x64.msi");
        a.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        a.InstallerSwitches = new InstallerSwitches { Silent = "/qn", Custom = "  " };
        Installer b = TestManifests.CreateInstaller(Architecture.X86, url: "https://example.com/app-x86.msi");
        b.InstallerSwitches = new InstallerSwitches { Silent = "/qn" };
        Installer duplicate = TestManifests.CreateInstaller(url: "https://example.com/app-x64.msi");
        PackageManifests manifests = TestManifests.Create(a, b, duplicate);
        ManifestContext context = TestManifests.CreateContext(manifests, explain: true);

        IReadOnlyList<RuleFinding> findings = RulePipeline.CreateDefault().Run(context);

        Assert.Empty(findings);
        Assert.Equal(2, manifests.Installer.Installers!.Count);
        Assert.Equal(InstallerType.Msi, manifests.Installer.InstallerType);
        Assert.Equal("/qn", manifests.Installer.InstallerSwitches?.Silent);
        Assert.Null(a.InstallerType);
        Assert.Null(a.InstallerSwitches);
        Assert.Null(b.InstallerSwitches);
        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", a.ProductCode);
        Assert.NotEmpty(context.Trace);
    }

    private sealed class UnsnapshotableOutputRule : IRule
    {
        public const string RuleId = "WM9990";

        public string Id => RuleId;

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Error;

        public string Description => "Produces a deliberately malformed graph for snapshot tests.";

        public void Apply(ManifestContext context)
        {
            context.Manifests.Installer.Installers!.Add(null!);
            context.Previous!.DefaultLocale.ShortDescription = "mutated";
            context.OriginalBotSubmission!.DefaultLocale.ShortDescription = "mutated";
        }
    }

    private sealed class InvocationTrackingRule : IRule
    {
        public string Id => "WM9989";

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Error;

        public string Description => "Tracks whether malformed input reached the rule.";

        public bool WasInvoked { get; private set; }

        public void Apply(ManifestContext context) => WasInvoked = true;
    }

    private sealed class StatefulRule : IRule
    {
        public string Id => "WM9988";

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Info;

        public string Description => "Changes output based on invocation count.";

        public int InvocationCount { get; private set; }

        public void Apply(ManifestContext context)
        {
            InvocationCount++;
            context.Manifests.Installer.Installers![0].ProductCode = $"RUN-{InvocationCount}";
        }
    }
}
