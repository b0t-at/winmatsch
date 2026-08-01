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

        return request switch
        {
            NewOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            UpdateOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            RemoveOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            SubmitOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            NewLocaleOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            UpdateLocaleOperationRequest value => value with
            {
                ApproveReview = true,
                ApprovedReviewFingerprints = fingerprints,
            },
            _ => throw new ArgumentException("Unsupported workflow request.", nameof(request)),
        };
    }

    internal static bool Matches(
        WorkflowOperationRequest request,
        RuleRunSummary summary)
    {
        if (!request.ApproveReview || summary.Reviews.IsEmpty)
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
