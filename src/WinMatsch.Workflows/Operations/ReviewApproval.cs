using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Rules;

namespace WinMatsch.Workflows.Operations;

public static class ReviewApproval
{
    public static WorkflowOperationRequest Bind(
        WorkflowOperationRequest request,
        LocalOperationPlan reviewedPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewedPlan);
        ImmutableArray<string> fingerprints = GetFingerprints(reviewedPlan.Rules);
        if (fingerprints.Length != reviewedPlan.Rules.Reviews.Length)
        {
            throw new InvalidOperationException(
                "Every reviewed human correction must carry a stable fingerprint before approval.");
        }

        string planFingerprint = CreatePlanFingerprint(reviewedPlan);
        return request switch
        {
            NewOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            UpdateOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            RemoveOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            SubmitOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            NewLocaleOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            UpdateLocaleOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
                ApprovedPlanFingerprint = planFingerprint,
            },
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
        };
    }

    internal static bool Matches(
        WorkflowOperationRequest request,
        RuleRunSummary summary,
        string planFingerprint)
    {
        if (!request.ApproveReview
            || summary.Reviews.IsEmpty
            || !string.Equals(
                request.ApprovedPlanFingerprint,
                planFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        ImmutableArray<string> actual = GetFingerprints(summary);
        return actual.Length == summary.Reviews.Length
            && request.ApprovedReviewFingerprints.SequenceEqual(actual, StringComparer.Ordinal);
    }

    internal static string CreatePlanFingerprint(LocalOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(plan.Operation);
        Add(plan.PackageIdentifier.Value);
        Add(plan.PackageVersion.Value);
        Add(Path.GetFullPath(plan.OutputDirectory));
        Add(plan.WarningPolicy.ToString());
        Add(plan.Preflight.Options.NetworkMode.ToString());
        Add($"rule-execution-count:{plan.Rules.Executions.Length}");
        foreach (RuleExecution execution in plan.Rules.Executions)
        {
            Add(execution.RuleId);
            Add(execution.Mode.ToString());
            Add(execution.ModeSource.ToString());
        }
        Add($"before-count:{plan.BeforeDocuments.Length}");
        foreach (RawManifestDocument document in plan.BeforeDocuments
                     .OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            Add("before");
            Add(document.RepositoryPath);
            Add(WorkflowFileChange.Hash(document.Content.AsSpan()));
        }

        Add($"after-count:{plan.AfterDocuments.Length}");
        foreach (RawManifestDocument document in plan.AfterDocuments
                     .OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            Add("after");
            Add(document.RepositoryPath);
            Add(WorkflowFileChange.Hash(document.Content.AsSpan()));
        }

        Add($"change-count:{plan.FileChanges.Length}");
        foreach (WorkflowFileChange change in plan.FileChanges
                     .OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            Add("change");
            Add(change.Kind.ToString());
            Add(change.RepositoryPath);
            Add(change.ExpectedState.ToString());
            Add(change.ExpectedSha256 ?? "<null>");
            Add(change.Provenance.ToString());
            Add(change.Kind == PlannedChangeKind.Delete
                ? "<deleted>"
                : WorkflowFileChange.Hash(change.Content.AsSpan()));
        }

        Add($"artifact-count:{plan.Preflight.InstallerArtifacts.Length}");
        foreach (var artifact in plan.Preflight.InstallerArtifacts
                     .OrderBy(static item => item.InstallerUrl, StringComparer.Ordinal))
        {
            Add("artifact");
            Add(artifact.InstallerUrl);
            Add(artifact.Download.Sha256.Value);
            Add(artifact.Download.SizeInBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(artifact.Download.FinalUrl);
            Add(artifact.Download.ETag ?? "<null>");
            Add(artifact.Download.LastModified?.ToString("O") ?? "<null>");
        }

        Add($"existing-version-count:{plan.Preflight.ExistingVersions.Count}");
        foreach (var existing in plan.Preflight.ExistingVersions
                     .OrderBy(static item => item.PackageVersion, StringComparer.Ordinal))
        {
            Add("existing-version");
            Add(existing.PackageVersion);
            string[] displayVersions =
            [
                .. existing.DisplayVersions.Order(StringComparer.Ordinal),
            ];
            Add($"display-version-count:{displayVersions.Length}");
            foreach (string displayVersion in displayVersions)
            {
                Add(displayVersion);
            }
            Add("existing-version-end");
        }

        if (plan.Release is { } release)
        {
            Add("release");
            Add(release.Repository.ToString());
            Add(release.ReleaseId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(release.UpdatedAt.ToString("O"));
        }

        return Convert.ToHexString(hash.GetHashAndReset());

        void Add(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private static ImmutableArray<string> GetFingerprints(RuleRunSummary summary)
        =>
        [
            .. summary.Reviews
                .Select(static review => review.CorrectionFingerprint)
                .Where(static fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
                .Select(static fingerprint => fingerprint!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
}
