using System.Collections.Immutable;
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

        string planFingerprint =
            LocalOperationPlanFingerprint.CreateApprovalFingerprint(reviewedPlan);
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
