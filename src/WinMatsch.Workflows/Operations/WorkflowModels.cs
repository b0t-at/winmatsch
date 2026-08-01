using System.Collections.Immutable;
using System.Security.Cryptography;
using WinMatsch.Core;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Operations;

public enum WorkflowResultCode
{
    Succeeded,
    QuestionsRequired,
    ReviewRequired,
    ValidationFailed,
    NoChanges,
    NotFound,
    Conflict,
    InvalidRequest,
    ApplyFailed,
}

public sealed record WorkflowQuestion(
    string Code,
    string Prompt,
    ImmutableArray<string> Options,
    string? Path = null);

public sealed record RawManifestDocument
{
    public RawManifestDocument(string repositoryPath, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        RepositoryPath = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        Content = [.. content];
    }

    public string RepositoryPath { get; }

    public ImmutableArray<byte> Content { get; }
}

public sealed record WorkflowFileChange
{
    public WorkflowFileChange(
        PlannedChangeKind kind,
        string repositoryPath,
        ReadOnlySpan<byte> content = default,
        ExpectedFileState expectedState = ExpectedFileState.Unspecified,
        string? expectedSha256 = null)
    {
        Kind = kind;
        RepositoryPath = WorkflowPath.NormalizeRepositoryPath(repositoryPath);
        Content = kind == PlannedChangeKind.Delete ? [] : [.. content];
        ExpectedState = expectedState == ExpectedFileState.Unspecified
            ? kind == PlannedChangeKind.Add
                ? ExpectedFileState.Absent
                : throw new ArgumentException(
                    "Updates and deletions require an expected existing-file state.",
                    nameof(expectedState))
            : expectedState;
        ExpectedSha256 = expectedSha256;
        if (expectedState == ExpectedFileState.Present
            && string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new ArgumentException(
                "Expected content SHA-256 is required for an existing file.",
                nameof(expectedSha256));
        }
    }

    public PlannedChangeKind Kind { get; }

    public string RepositoryPath { get; }

    public ImmutableArray<byte> Content { get; }

    public ExpectedFileState ExpectedState { get; }

    public string? ExpectedSha256 { get; }

    public static string Hash(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content));
}

public enum ExpectedFileState
{
    Unspecified,
    Absent,
    Present,
}

public sealed record WorkflowAuditEntry(string Code, string Message, string? Provenance = null);

public sealed record RuleRunSummary(
    ImmutableArray<RuleExecution> Executions,
    ImmutableArray<RuleChange> Changes,
    ImmutableArray<RuleFinding> Findings,
    ImmutableArray<HumanCorrectionReview> Reviews,
    ImmutableArray<RuleTraceEntry> Trace)
{
    public static RuleRunSummary Empty { get; } = new([], [], [], [], []);

    public bool RequiresReview => !Reviews.IsEmpty;
}

public sealed record LocalOperationPlan
{
    public required string Operation { get; init; }

    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public required string OutputDirectory { get; init; }

    public required ImmutableArray<WorkflowFileChange> FileChanges { get; init; }

    public required ImmutableArray<RawManifestDocument> BeforeDocuments { get; init; }

    public required ImmutableArray<RawManifestDocument> AfterDocuments { get; init; }

    public required ValidationReport Validation { get; init; }

    public WarningPolicy WarningPolicy { get; init; }

    public required WorkflowPreflightRequest Preflight { get; init; }

    public required RuleRunSummary Rules { get; init; }

    public ImmutableArray<WorkflowQuestion> Questions { get; init; } = [];

    public ImmutableArray<WorkflowAuditEntry> Audit { get; init; } = [];

    public bool ReviewApproved { get; init; }

    public bool RequiresReview => Rules.RequiresReview && !ReviewApproved;

    public bool CanApply => FileChanges.Length > 0
        && Questions.IsEmpty
        && Validation.CanProceed(WarningPolicy)
        && !RequiresReview;

    public OperationPlan ToOperationPlan()
    {
        var findings = new List<ValidationFinding>(Validation.Findings);
        findings.AddRange(Questions.Select(static question => new ValidationFinding(
            question.Code,
            ValidationSeverity.Error,
            question.Prompt,
            question.Path)));
        if (RequiresReview)
        {
            findings.Add(new ValidationFinding(
                "WF_REVIEW_REQUIRED",
                ValidationSeverity.Error,
                "Known human corrections require explicit approval."));
        }

        if (WarningPolicy == WarningPolicy.TreatAsErrors)
        {
            findings.AddRange(Validation.Findings
                .Where(static finding => finding.Severity == ValidationSeverity.Warning)
                .Select(static finding => finding with
                {
                    Code = $"BLOCKING_{finding.Code}",
                    Severity = ValidationSeverity.Error,
                }));
        }

        return new(
            Operation,
            FileChanges.Select(static change => new PlannedChange(
                change.Kind,
                change.RepositoryPath,
                $"{change.Kind} {change.RepositoryPath}")),
            new ValidationReport(findings));
    }
}

public sealed record WorkflowOperationResult
{
    public required WorkflowResultCode Code { get; init; }

    public required LocalOperationPlan Plan { get; init; }

    public bool Applied { get; init; }

    public string? ErrorMessage { get; init; }
}

public abstract record WorkflowOperationRequest
{
    public WorkflowExecutionMode ExecutionMode { get; init; } = WorkflowExecutionMode.Plan;

    public required string OutputDirectory { get; init; }

    public string CreatedWith { get; init; } = "winmatsch";

    public WarningPolicy WarningPolicy { get; init; } = WarningPolicy.Allow;

    public NetworkValidationMode NetworkValidationMode { get; init; } = NetworkValidationMode.Online;

    public RuleRuntimeConfiguration RuleRuntime { get; init; } = new();

    public OverridePackSet OverridePacks { get; init; } = OverridePackSet.Empty;

    public PolicyEvidence PolicyEvidence { get; init; } = PolicyEvidence.Empty;

    public bool ExplainRules { get; init; }

    public bool ApproveReview { get; init; }
}

public sealed record PackageLocaleMetadata
{
    public required LanguageTag PackageLocale { get; init; }

    public string? Publisher { get; init; }

    public string? PublisherUrl { get; init; }

    public string? PublisherSupportUrl { get; init; }

    public string? PrivacyUrl { get; init; }

    public string? Author { get; init; }

    public string? PackageName { get; init; }

    public string? PackageUrl { get; init; }

    public string? License { get; init; }

    public string? LicenseUrl { get; init; }

    public string? Copyright { get; init; }

    public string? CopyrightUrl { get; init; }

    public string? ShortDescription { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string? ReleaseNotes { get; init; }

    public string? ReleaseNotesUrl { get; init; }
}

public sealed record ReleaseRequest(
    string? Release,
    ImmutableArray<Uri> InstallerUrls,
    ImmutableArray<Uri> ReleaseUrls);

public sealed record NewOperationRequest : WorkflowOperationRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public string? PackageVersion { get; init; }

    public ReleaseRequest Release { get; init; } = new(null, [], []);

    public ImmutableArray<DiscoveredAsset> Assets { get; init; } = [];

    public required PackageLocaleMetadata Locale { get; init; }

    public ImmutableArray<UrlOverride> UrlOverrides { get; init; } = [];

    public bool AllowSharedContentAcrossUrls { get; init; }

    public string? ArtifactDirectory { get; init; }

    public ImmutableArray<InstallerArtifact> InstallerArtifacts { get; init; } = [];
}

public sealed record UpdateOperationRequest : WorkflowOperationRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PreviousVersion { get; init; }

    public string? PackageVersion { get; init; }

    public ReleaseRequest Release { get; init; } = new(null, [], []);

    public ImmutableArray<DiscoveredAsset> Assets { get; init; } = [];

    public ImmutableArray<UrlOverride> UrlOverrides { get; init; } = [];

    public bool ReplacePreviousVersion { get; init; }

    public bool AllowStructuralRewrite { get; init; }

    public bool AllowStableUrlContentChange { get; init; }

    public bool AllowSharedContentAcrossUrls { get; init; }

    public string? ArtifactDirectory { get; init; }

    public ImmutableArray<InstallerArtifact> InstallerArtifacts { get; init; } = [];
}

public sealed record RemoveOperationRequest : WorkflowOperationRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }
}

public sealed record SubmitOperationRequest : WorkflowOperationRequest
{
    public required ImmutableArray<RawManifestDocument> Documents { get; init; }

    public bool Normalize { get; init; }

    public string? ArtifactDirectory { get; init; }
}

public sealed record NewLocaleOperationRequest : WorkflowOperationRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public required PackageLocaleMetadata Locale { get; init; }
}

public sealed record UpdateLocaleOperationRequest : WorkflowOperationRequest
{
    public required PackageIdentifier PackageIdentifier { get; init; }

    public required PackageVersion PackageVersion { get; init; }

    public required PackageLocaleMetadata Locale { get; init; }
}

internal static class WorkflowPath
{
    public static string NormalizeRepositoryPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Repository paths must be non-rooted and traversal-free.", nameof(path));
        }

        return normalized;
    }
}
