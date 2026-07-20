using WinMatsch.Core;
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
            new[]
            {
                RuleIds.PreserveOnUpdate,
                RuleIds.ApplyPackageQuirks,
                RuleIds.PushDownRootFields,
                RuleIds.ScrubEmptyStrings,
                RuleIds.NormalizeProductCodes,
                RuleIds.DedupeArpVsDefaultLocale,
                RuleIds.RemoveDuplicateInstallers,
                RuleIds.HoistCommonInstallerFields,
                RuleIds.DisplayVersionConsistency,
                RuleIds.DuplicateInstallerEntries,
                RuleIds.InstallerTypeConsistency,
            },
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
}
