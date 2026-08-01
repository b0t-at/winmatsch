using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;

namespace WinMatsch.Workflows.Operations;

public sealed record PackageSnapshot
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public required string VersionDirectory { get; init; }

    public required PackageManifests Manifests { get; init; }

    public PackageManifests? OriginalBotSubmission { get; init; }

    public required ImmutableArray<RawManifestDocument> Documents { get; init; }
}

public interface IManifestSnapshotSource
{
    public Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken);
}

public interface IWorkflowReleaseSource
{
    public Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken);
}

public sealed record ArtifactSnapshot
{
    public required DiscoveredAsset Asset { get; init; }

    public required DownloadResult Download { get; init; }

    public required InstallerAnalysis Analysis { get; init; }

    public PayloadDependencyAnalysis? DependencyAnalysis { get; init; }

    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

public interface IWorkflowArtifactProcessor
{
    public Task<ArtifactSnapshot> AcquireAsync(
        DiscoveredAsset asset,
        string artifactDirectory,
        CancellationToken cancellationToken);
}

public sealed record WorkflowRuleRequest
{
    public required PackageManifests Manifests { get; init; }

    public PackageManifests? Previous { get; init; }

    public PackageManifests? OriginalBotSubmission { get; init; }

    public ImmutableArray<InstallerEvidence> InstallerEvidence { get; init; } = [];

    public required RuleRuntimeConfiguration Runtime { get; init; }

    public required OverridePackSet OverridePacks { get; init; }

    public required PolicyEvidence PolicyEvidence { get; init; }

    public RuleOptions Options { get; init; } = new();
}

public sealed record WorkflowRuleResult(
    PackageManifests Manifests,
    RuleRunSummary Summary);

public interface IWorkflowRuleRunner
{
    public WorkflowRuleResult Run(WorkflowRuleRequest request);
}

public sealed record WorkflowPreflightRequest
{
    public required ImmutableArray<RawManifestDocument> BeforeDocuments { get; init; }

    public required ImmutableArray<RawManifestDocument> AfterDocuments { get; init; }

    public required ImmutableArray<WorkflowFileChange> Changes { get; init; }

    public ImmutableArray<InstallerArtifact> InstallerArtifacts { get; init; } = [];

    public IReadOnlyList<ExistingVersionSnapshot> ExistingVersions { get; init; } = [];

    public PreflightOptions Options { get; init; } = new();
}

public interface IWorkflowPreflight
{
    public Task<ValidationReport> ValidateAsync(
        WorkflowPreflightRequest request,
        CancellationToken cancellationToken);
}

public interface IWorkflowFileTransaction
{
    public Task ApplyAsync(
        string outputDirectory,
        string operationLockKey,
        ImmutableArray<WorkflowFileChange> changes,
        CancellationToken cancellationToken);
}

public interface IWorkflowClock
{
    public DateTimeOffset UtcNow { get; }
}

public sealed class SystemWorkflowClock : IWorkflowClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class RulePipelineWorkflowRunner(
    Func<PolicyEvidence, OverridePackSet, IReadOnlyList<IRule>> composer) : IWorkflowRuleRunner
{
    private readonly Func<PolicyEvidence, OverridePackSet, IReadOnlyList<IRule>> _composer =
        composer ?? throw new ArgumentNullException(nameof(composer));

    public WorkflowRuleResult Run(WorkflowRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<IRule> rules = _composer(request.PolicyEvidence, request.OverridePacks);
        RulePipeline pipeline = RulePipeline.Create(rules, request.Runtime, request.OverridePacks);
        var context = new ManifestContext
        {
            Manifests = request.Manifests,
            Previous = request.Previous,
            OriginalBotSubmission = request.OriginalBotSubmission,
            Evidence = request.InstallerEvidence,
            Options = request.Options,
        };
        _ = pipeline.Run(context);
        return new(
            context.Manifests,
            new(
                [.. context.Executions],
                [.. context.Changes],
                [.. context.Findings],
                [.. context.HumanCorrectionReviews],
                [.. context.Trace]));
    }
}

public sealed class PreflightGateWorkflowAdapter(PreflightGate gate) : IWorkflowPreflight
{
    private readonly PreflightGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public Task<ValidationReport> ValidateAsync(
        WorkflowPreflightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterDocuments.IsEmpty
            && !request.BeforeDocuments.IsEmpty
            && request.Changes.All(static change => change.Kind == PlannedChangeKind.Delete))
        {
            return Task.FromResult(new ValidationReport());
        }

        return _gate.ValidateAsync(
            new PreflightRequest
            {
                Documents =
                [
                    .. request.AfterDocuments.Select(static document => new ManifestDocument(
                        document.RepositoryPath,
                        StrictUtf8.Decode(document.Content.AsSpan()))),
                ],
                Changes =
                [
                    .. request.Changes.Select(static change => new RepositoryFileChange(
                        change.RepositoryPath,
                        change.Kind switch
                        {
                            PlannedChangeKind.Add => RepositoryChangeKind.Added,
                            PlannedChangeKind.Update => RepositoryChangeKind.Modified,
                            PlannedChangeKind.Delete => RepositoryChangeKind.Deleted,
                            _ => throw new ArgumentOutOfRangeException(nameof(request)),
                        })),
                ],
                ExistingVersions = request.ExistingVersions,
                InstallerArtifacts = request.InstallerArtifacts,
                Options = request.Options,
            },
            cancellationToken);
    }
}

internal static class StrictUtf8
{
    private static readonly System.Text.UTF8Encoding _encoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(ReadOnlySpan<byte> content) => _encoding.GetString(content);

    public static byte[] Encode(string content) => _encoding.GetBytes(content);
}
