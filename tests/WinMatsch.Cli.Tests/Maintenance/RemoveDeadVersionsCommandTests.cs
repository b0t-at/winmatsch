using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.Workflows.GitHub;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class RemoveDeadVersionsCommandTests
{
    private const string Package = "Contoso.App";

    [Fact]
    public async Task Dry_run_reports_a_removable_dead_version()
    {
        FakeDeadVersionInspector inspector = Inspecting(
            ("1.0.0", Dead()));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(
            ["remove-dead-versions", Package, "1.0.0", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Contoso.App 1.0.0: removable", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Escalation", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Transient_failures_escalate_instead_of_removing()
    {
        FakeDeadVersionInspector inspector = Inspecting(
            ("1.0.0", new DeadVersionInspection(
                Identifier(),
                new PackageVersion("1.0.0"),
                ExistsUpstream: true,
                [DeadArtifactState.PermanentlyMissing, DeadArtifactState.TransientFailure])));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("not removable", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GH3103", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Escalation", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(harness.Interaction.Questions);
    }

    [Fact]
    public async Task Network_blocked_artifacts_escalate()
    {
        FakeDeadVersionInspector inspector = Inspecting(
            ("1.0.0", new DeadVersionInspection(
                Identifier(),
                new PackageVersion("1.0.0"),
                ExistsUpstream: true,
                [DeadArtifactState.NetworkBlocked])));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("GH3103", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Versions_missing_upstream_are_not_removable()
    {
        FakeDeadVersionInspector inspector = Inspecting(
            ("1.0.0", new DeadVersionInspection(
                Identifier(),
                new PackageVersion("1.0.0"),
                ExistsUpstream: false,
                [])));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("GH3102", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_versions_are_rejected_before_remote_inspection()
    {
        FakeDeadVersionInspector inspector = Inspecting(
            ("1.0.0", Dead()),
            ("1.1.0", Dead("1.1.0")));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(
            ["remove-dead-versions", Package, "1.0.0", "1.1.0", "--yes"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(0, inspector.InspectCallCount);
    }

    [Fact]
    public async Task Apply_requires_confirmation()
    {
        FakeDeadVersionInspector inspector = Inspecting(("1.0.0", Dead()));
        CliHarness harness = CreateHarness(inspector);
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("--yes", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_confirmation_aborts()
    {
        FakeDeadVersionInspector inspector = Inspecting(("1.0.0", Dead()));
        CliHarness harness = CreateHarness(inspector);
        harness.Interaction.EnqueueConfirm(false);

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(1, inspector.InspectCallCount);
    }

    [Fact]
    public async Task Apply_revalidates_and_escalates_for_manual_submission()
    {
        FakeDeadVersionInspector inspector = Inspecting(("1.0.0", Dead()));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(["remove-dead-versions", Package, "1.0.0", "--yes"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(2, inspector.InspectCallCount);
        Assert.Contains("Escalation", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Human escalation required", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("one removal pull request per", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revalidation_indeterminate_result_remains_escalated()
    {
        var inspector = new FakeDeadVersionInspector();
        inspector.InspectionSequences["1.0.0"] = new Queue<DeadVersionInspection>(
        [
            Dead(),
            new(
                Identifier(),
                new PackageVersion("1.0.0"),
                ExistsUpstream: true,
                [DeadArtifactState.TransientFailure]),
        ]);
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(
            ["remove-dead-versions", Package, "1.0.0", "--yes", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("\"humanEscalationRequired\":true", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GH3103", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_output_is_stable()
    {
        FakeDeadVersionInspector inspector = Inspecting(("1.0.0", Dead()));
        CliHarness harness = CreateHarness(inspector);

        CliRunResult result = await harness.RunAsync(
            ["remove-dead-versions", Package, "1.0.0", "--dry-run", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith(
            "{\"schemaVersion\":\"1.0\",\"operation\":\"remove-dead-versions\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("\"canRemove\":true", result.StandardOutput, StringComparison.Ordinal);
    }

    private static PackageIdentifier Identifier() => new(Package);

    private static DeadVersionInspection Dead(string version = "1.0.0")
        => new(
            Identifier(),
            new PackageVersion(version),
            ExistsUpstream: true,
            [DeadArtifactState.PermanentlyMissing]);

    private static FakeDeadVersionInspector Inspecting(
        params (string Version, DeadVersionInspection Inspection)[] inspections)
    {
        var inspector = new FakeDeadVersionInspector();
        foreach ((string version, DeadVersionInspection inspection) in inspections)
        {
            inspector.Inspections[version] = inspection;
        }

        return inspector;
    }

    private static CliHarness CreateHarness(FakeDeadVersionInspector inspector)
    {
        var harness = new CliHarness();
        harness.EnvironmentVariables["GITHUB_TOKEN"] = "test-token-value";
        harness.Modules.Add(new MaintenanceCommandModule(
            clientFactory: _ => new FakeMaintenanceGitHubClient(),
            inspectorFactory: _ => inspector));
        return harness;
    }
}
