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

    public bool IsRemote { get; init; }
}

public interface IManifestSnapshotSource
{
    public Task<PackageSnapshot?> LoadAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        CancellationToken cancellationToken);

    public Task<ImmutableArray<PackageSnapshot>> ListVersionsAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
}

internal interface IManifestSnapshotSourceDiagnosticSource
{
    public string? GetListVersionsDiagnostic(PackageIdentifier packageIdentifier);

    public string? GetLoadDiagnostic(
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion);
}

public interface IWorkflowReleaseSource
{
    public Task<ImmutableArray<DiscoveredAsset>> DiscoverAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        CancellationToken cancellationToken);
}

public interface IWorkflowReleaseMetadataSource
{
    public Task<WorkflowReleaseMetadata> DiscoverMetadataAsync(
        PackageIdentifier packageIdentifier,
        ReleaseRequest request,
        ImmutableArray<DiscoveredAsset> assets,
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

    public Task<ValidationReport> ExecuteAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken);
}

public interface IWorkflowVerifiedPreflight : IWorkflowPreflight
{
    public Task<ValidationReport> ExecuteVerifiedAsync(
        WorkflowPreflightRequest request,
        Func<ValidationReport, CancellationToken, Task> boundary,
        CancellationToken cancellationToken);
}

internal interface IWorkflowPreflightDiagnosticSource
{
    public ImmutableArray<ValidationFinding> DrainDiagnostics();
}

public interface IWorkflowFileTransaction
{
    public Task ApplyAsync(
        string outputDirectory,
        string operationLockKey,
        ImmutableArray<WorkflowFileChange> changes,
        CancellationToken cancellationToken);
}

public interface IWorkflowFileTransactionRecovery
{
    public Task RecoverAsync(
        string outputDirectory,
        string operationLockKey,
        CancellationToken cancellationToken);
}

public interface IWorkflowCoordinatedRecovery : IWorkflowFileTransactionRecovery
{
    public Task<IDisposable> RecoverAndHoldAsync(
        string outputDirectory,
        string operationLockKey,
        CancellationToken cancellationToken);
}

public interface ILocalOperationLockProvider
{
    public ValueTask<IAsyncDisposable> AcquireAsync(
        string outputDirectory,
        PackageIdentifier packageIdentifier,
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

public sealed class PreflightGateWorkflowAdapter : IWorkflowVerifiedPreflight
{
    private readonly PreflightGate _gate;
    private readonly IWorkflowPreflightDiagnosticSource? _diagnostics;

    public PreflightGateWorkflowAdapter(PreflightGate gate)
        : this(gate, diagnostics: null)
    {
    }

    internal PreflightGateWorkflowAdapter(
        PreflightGate gate,
        IWorkflowPreflightDiagnosticSource? diagnostics)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _diagnostics = diagnostics;
    }

    public async Task<ValidationReport> ValidateAsync(
        WorkflowPreflightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterDocuments.IsEmpty
            && !request.BeforeDocuments.IsEmpty
            && request.Changes.All(static change => change.Kind == PlannedChangeKind.Delete))
        {
            return AppendDiagnostics(new ValidationReport());
        }

        ValidationReport report = await _gate.ValidateAsync(CreateRequest(request), cancellationToken)
            .ConfigureAwait(false);
        report = IsInstallerUnchanged(request) ? RemoveArtifactRevalidationFindings(report) : report;
        return AppendDiagnostics(report);
    }

    public async Task<ValidationReport> ExecuteAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        if (request.AfterDocuments.IsEmpty
            && !request.BeforeDocuments.IsEmpty
            && request.Changes.All(static change => change.Kind == PlannedChangeKind.Delete))
        {
            return AppendDiagnostics(
                await ExecuteRemovalAsync(request, boundary, cancellationToken).ConfigureAwait(false));
        }

        ValidationReport report = IsInstallerUnchanged(request)
            ? await ExecuteUnchangedInstallerAsync(request, boundary, cancellationToken).ConfigureAwait(false)
            : await _gate.ExecuteAsync(
                CreateRequest(request),
                new DelegatePreflightBoundary(boundary),
                cancellationToken).ConfigureAwait(false);
        return AppendDiagnostics(report);
    }

    public async Task<ValidationReport> ExecuteVerifiedAsync(
        WorkflowPreflightRequest request,
        Func<ValidationReport, CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ValidationReport report = await ValidateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (report.CanProceed(request.Options.WarningPolicy))
        {
            await boundary(report, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    private static async Task<ValidationReport> ExecuteRemovalAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        ValidationReport report = new();
        if (report.CanProceed(request.Options.WarningPolicy))
        {
            await boundary(cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    private async Task<ValidationReport> ExecuteUnchangedInstallerAsync(
        WorkflowPreflightRequest request,
        Func<CancellationToken, Task> boundary,
        CancellationToken cancellationToken)
    {
        ValidationReport report = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (report.CanProceed(request.Options.WarningPolicy))
        {
            await boundary(cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    private static bool IsInstallerUnchanged(WorkflowPreflightRequest request)
    {
        RawManifestDocument? before = request.BeforeDocuments.SingleOrDefault(IsInstaller);
        RawManifestDocument? after = request.AfterDocuments.SingleOrDefault(IsInstaller);
        return before is not null
            && after is not null
            && string.Equals(before.RepositoryPath, after.RepositoryPath, StringComparison.Ordinal)
            && before.Content.AsSpan().SequenceEqual(after.Content.AsSpan());

        static bool IsInstaller(RawManifestDocument document)
            => document.RepositoryPath.EndsWith(".installer.yaml", StringComparison.Ordinal);
    }

    private static ValidationReport RemoveArtifactRevalidationFindings(ValidationReport report)
        => new(report.Findings.Where(static finding =>
            finding.Code is not ("VLD6001" or "VLD6002" or "VLD6005")));

    private ValidationReport AppendDiagnostics(ValidationReport report)
        => _diagnostics is null
            ? report
            : new ValidationReport([.. report.Findings, .. _diagnostics.DrainDiagnostics()]);

    private static PreflightRequest CreateRequest(WorkflowPreflightRequest request)
        => new()
        {
            Documents =
            [
                .. request.AfterDocuments.Select(static document => new ManifestDocument(
                    document.RepositoryPath,
                    StrictUtf8.Decode(document.Content.AsSpan()))),
            ],
            Changes =
            [
                .. request.Changes
                    .Where(change => change.Kind != PlannedChangeKind.Delete
                        || request.AfterDocuments.Any(document =>
                            document.RepositoryPath.StartsWith(
                                VersionDirectory(document.RepositoryPath),
                                StringComparison.Ordinal)
                            && change.RepositoryPath.StartsWith(
                                VersionDirectory(document.RepositoryPath),
                                StringComparison.Ordinal)))
                    .Select(static change => new RepositoryFileChange(
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
        };

    private static string VersionDirectory(string path)
    {
        string[] segments = path.Split('/');
        return segments.Length < 5 ? path : string.Join('/', segments.Take(segments.Length - 1)) + "/";
    }

    private sealed class DelegatePreflightBoundary(Func<CancellationToken, Task> boundary) : IPreflightBoundary
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => boundary(cancellationToken);
    }
}

internal static class StrictUtf8
{
    private static readonly System.Text.UTF8Encoding _encoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(ReadOnlySpan<byte> content) => _encoding.GetString(content);

    public static byte[] Encode(string content) => _encoding.GetBytes(content);
}
