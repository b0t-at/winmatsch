using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Dep2DependencyOutageRuleTests
{
    [Fact]
    public void Outage_signature_is_classified_as_infrastructure()
    {
        // Motivating regression: VCRedist index outage stale-killed 4KVideoDownloaderPlus PRs
        // (microsoft/winget-pkgs#152555; PRs #154036, #157370, ...).
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts =
            [
                "##[error] No suitable installer found for manifest Microsoft.VCRedist.2015+.x64 with version 14.38.33135.0",
            ],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Equal(RuleCatalogueIds.Dep2, finding.RuleId);
        Assert.Contains("infrastructure", finding.Message, StringComparison.Ordinal);
        Assert.Contains("do not mutate the manifest", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_pipeline_errors_are_not_classified()
    {
        // Nonmatching control: a genuine manifest error must not be waved through as infra.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts = ["##[error] Manifest Error: Duplicate installer entry found."],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Typoed_runtime_identifiers_are_not_classified()
    {
        // Only the exact known package shapes match; Microsoft.DotNetBogus stays a manifest error.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts =
            [
                "No suitable installer found for manifest Microsoft.DotNetBogus with version 1.0.0",
                "No suitable installer found for manifest Microsoft.VCRedist.2015+.mips with version 14.0.0",
            ],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Outage_signature_for_a_non_runtime_package_is_not_classified()
    {
        // Only the well-known runtime dependency identifiers count as the outage signature;
        // a genuinely wrong dependency must not be waved through as infrastructure.
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts = ["No suitable installer found for manifest Some.Package with version 1.0.0"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void The_rule_never_mutates_the_manifest()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts =
            [
                "No suitable installer found for manifest Microsoft.DotNet.Runtime.8 with version 8.0.0",
            ],
        });

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.Apply);

        Assert.Empty(context.Changes);
        Assert.Single(context.Findings);
    }

    [Fact]
    public void Without_log_excerpts_nothing_happens()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Dep2DependencyOutageRule().Apply(context);

        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        var rule = new Dep2DependencyOutageRule(new PolicyEvidence
        {
            PipelineLogExcerpts =
            [
                "No suitable installer found for manifest Microsoft.VCRedist.2015+.x64 with version 14.38.33135.0",
            ],
        });

        ManifestContext context = PolicyTestSupport.RunViaPipeline(rule, manifests, RuleMode.Disabled);

        Assert.Empty(context.Findings);
    }
}
