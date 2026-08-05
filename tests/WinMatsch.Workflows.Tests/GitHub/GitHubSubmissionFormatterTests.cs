using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class GitHubSubmissionFormatterTests
{
    [Fact]
    public void Add_body_without_optional_details_is_concise_and_versioned()
    {
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(
            WorkflowExecutionMode.Plan) with
        {
            Operation = GitHubManifestOperation.Add,
            CreatedWith = "winmatsch",
        };
        Version version = typeof(GitHubSubmissionFormatter).Assembly.GetName().Version!;

        string body = GitHubSubmissionFormatter.CreateBody(
            request,
            "manifests/e/Example/App/2.0.0").ReplaceLineEndings("\n");

        Assert.Equal(
            $"""
            <!-- winmatsch:package=Example.App;version=2.0.0 -->
            <!-- winmatsch:operation=Add -->
            Add Example.App version 2.0.0.
            Created with winmatsch v{version.Major}.{version.Minor}.{version.Build}
            """,
            body);
        Assert.DoesNotContain("Operation:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Target repository:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Manifest path:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Dry run:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Skip PR check:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("## Rules", body, StringComparison.Ordinal);
        Assert.DoesNotContain("## Changes", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_body_includes_custom_attribution_and_resolution_but_no_validation_details()
    {
        LocalOperationPlan plan = GitHubLifecycleTestSupport.Plan() with
        {
            Validation = new ValidationReport(
            [
                new("VLD1000", ValidationSeverity.Info, "Informational detail."),
                new("VLD3007", ValidationSeverity.Warning, "Declare a PortableCommandAlias."),
            ]),
        };
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request(
            WorkflowExecutionMode.Apply) with
        {
            LocalPlan = plan,
            CreatedWith = "[Komac](https://github.com/russellbanks/Komac)",
            Resolves = "32",
            VanityUrlAnnotations = ["Stable vendor URL revalidated."],
            Policy = new()
            {
                DuplicateHashes = new()
                {
                    OverrideAnnotation = "Repository steward approved shared vendor payload.",
                },
            },
        };

        string body = GitHubSubmissionFormatter.CreateBody(
            request,
            "manifests/e/Example/App/2.0.0").ReplaceLineEndings("\n");

        Assert.Equal(
            """
            <!-- winmatsch:package=Example.App;version=2.0.0 -->
            <!-- winmatsch:operation=Update -->
            Update Example.App version 2.0.0.
            Created with [Komac](https://github.com/russellbanks/Komac)
            Resolves #32
            """,
            body);
        Assert.DoesNotContain("VLD1000", body, StringComparison.Ordinal);
        Assert.DoesNotContain("VLD3007", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal validation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Stable vendor URL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate-hash override", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_and_replace_use_reviewer_facing_actions()
    {
        GitHubSubmissionRequest request = GitHubLifecycleTestSupport.Request() with
        {
            Operation = GitHubManifestOperation.Remove,
        };

        string remove = GitHubSubmissionFormatter.CreateBody(request, "unused");
        string replace = GitHubSubmissionFormatter.CreateBody(
            request with { Operation = GitHubManifestOperation.Replace },
            "unused");

        Assert.Contains("Remove Example.App version 2.0.0.", remove, StringComparison.Ordinal);
        Assert.Contains("Update Example.App version 2.0.0.", replace, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- winmatsch:operation=Replace -->", GitHubManifestOperation.Replace)]
    [InlineData("Operation: Replace", GitHubManifestOperation.Replace)]
    public void Operation_contract_accepts_new_and_legacy_bodies(
        string body,
        GitHubManifestOperation expected)
    {
        Assert.True(GitHubSubmissionFormatter.TryGetOperation(body, out GitHubManifestOperation actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("<!-- winmatsch:operation=4 -->")]
    [InlineData("<!-- winmatsch:operation=999 -->")]
    [InlineData("<!-- winmatsch:operation=Update, Replace -->")]
    [InlineData("Operation: 4")]
    public void Operation_contract_rejects_non_named_values(string body)
    {
        Assert.True(GitHubSubmissionFormatter.HasOperationMetadata(body));
        Assert.False(GitHubSubmissionFormatter.TryGetOperation(body, out _));
    }
}
