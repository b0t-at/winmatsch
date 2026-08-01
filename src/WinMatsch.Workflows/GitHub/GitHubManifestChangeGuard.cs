using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public static class GitHubManifestChangeGuard
{
    public static ImmutableArray<GitHubLifecycleDiagnostic> Validate(
        LocalOperationPlan plan,
        GitHubSubmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policy);
        var diagnostics = ImmutableArray.CreateBuilder<GitHubLifecycleDiagnostic>();
        string expectedVersion = ManifestPaths.GetVersionDirectory(
            plan.PackageIdentifier,
            plan.PackageVersion);
        string packageDirectory = ManifestPaths.GetPackageDirectory(plan.PackageIdentifier);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!plan.CanApply)
        {
            diagnostics.Add(new(
                "GH1001",
                "The local operation plan is not commit-ready."));
        }

        if (plan.FileChanges.IsEmpty)
        {
            diagnostics.Add(new("GH1002", "A GitHub submission must contain a non-empty diff."));
            return diagnostics.ToImmutable();
        }

        ValidatePreflightBinding(plan, diagnostics);
        var changedVersionDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkflowFileChange change in plan.FileChanges)
        {
            if (!paths.Add(change.RepositoryPath))
            {
                diagnostics.Add(new(
                    "GH1008",
                    "The diff contains duplicate or case-colliding repository paths.",
                    change.RepositoryPath));
            }

            string? versionDirectory = GetVersionDirectory(change.RepositoryPath);
            if (versionDirectory is null
                || !change.RepositoryPath.StartsWith("manifests/", StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "GH1003",
                    "Only canonical manifest version paths may be submitted.",
                    change.RepositoryPath));
                continue;
            }

            string canonical = change.Kind == PlannedChangeKind.Delete
                && policy.ReplacePreviousVersion
                && policy.PreviousVersion is not null
                ? ManifestPaths.GetVersionDirectory(plan.PackageIdentifier, policy.PreviousVersion)
                : expectedVersion;
            if (!string.Equals(versionDirectory, canonical, StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "GH1004",
                    "The diff contains an off-target package or version path.",
                    change.RepositoryPath));
            }

            if (!versionDirectory.StartsWith(packageDirectory + "/", StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "GH1005",
                    "Manifest path casing or package identity differs from the planned package.",
                    change.RepositoryPath));
            }

            if (change.Kind == PlannedChangeKind.Delete
                && !string.Equals(versionDirectory, expectedVersion, StringComparison.Ordinal)
                && (!policy.ReplacePreviousVersion
                    || policy.PreviousVersion is null
                    || !string.Equals(
                        versionDirectory,
                        ManifestPaths.GetVersionDirectory(plan.PackageIdentifier, policy.PreviousVersion),
                        StringComparison.Ordinal)))
            {
                diagnostics.Add(new(
                    "GH1006",
                    "Deleting a prior version is allowed only for an explicit replace operation.",
                    change.RepositoryPath));
            }

            changedVersionDirectories.Add(versionDirectory);
            ValidateDocumentAssociation(plan, change, diagnostics);
        }

        int allowedDirectoryCount = policy.ReplacePreviousVersion && policy.PreviousVersion is not null ? 2 : 1;
        if (changedVersionDirectories.Count > allowedDirectoryCount)
        {
            diagnostics.Add(new(
                "GH1007",
                "The diff spans multiple package versions outside the allowed replace pair."));
        }

        return diagnostics.ToImmutable();
    }

    private static void ValidatePreflightBinding(
        LocalOperationPlan plan,
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder diagnostics)
    {
        WorkflowPreflightRequest preflight = plan.Preflight;
        if (!DocumentsEqual(plan.BeforeDocuments, preflight.BeforeDocuments))
        {
            diagnostics.Add(new(
                "GH1019",
                "Preflight before-documents do not exactly match the commit plan."));
        }

        if (!DocumentsEqual(plan.AfterDocuments, preflight.AfterDocuments))
        {
            diagnostics.Add(new(
                "GH1020",
                "Preflight after-documents do not exactly match the commit plan."));
        }

        if (!ChangesEqual(plan.FileChanges, preflight.Changes))
        {
            diagnostics.Add(new(
                "GH1021",
                "Preflight file changes do not exactly match the commit plan."));
        }
    }

    private static bool DocumentsEqual(
        ImmutableArray<RawManifestDocument> left,
        ImmutableArray<RawManifestDocument> right)
        => left.Length == right.Length
            && left.Zip(right, static (first, second) =>
                string.Equals(first.RepositoryPath, second.RepositoryPath, StringComparison.Ordinal)
                && first.Content.AsSpan().SequenceEqual(second.Content.AsSpan()))
                .All(static equal => equal);

    private static bool ChangesEqual(
        ImmutableArray<WorkflowFileChange> left,
        ImmutableArray<WorkflowFileChange> right)
        => left.Length == right.Length
            && left.Zip(right, static (first, second) =>
                first.Kind == second.Kind
                && string.Equals(first.RepositoryPath, second.RepositoryPath, StringComparison.Ordinal)
                && first.Content.AsSpan().SequenceEqual(second.Content.AsSpan())
                && first.ExpectedState == second.ExpectedState
                && string.Equals(first.ExpectedSha256, second.ExpectedSha256, StringComparison.Ordinal)
                && first.Provenance == second.Provenance)
                .All(static equal => equal);

    private static void ValidateDocumentAssociation(
        LocalOperationPlan plan,
        WorkflowFileChange change,
        ImmutableArray<GitHubLifecycleDiagnostic>.Builder diagnostics)
    {
        RawManifestDocument[] after = [.. plan.AfterDocuments.Where(document =>
            string.Equals(document.RepositoryPath, change.RepositoryPath, StringComparison.Ordinal))];
        if (change.Kind == PlannedChangeKind.Delete)
        {
            if (after.Length != 0)
            {
                diagnostics.Add(new(
                    "GH1009",
                    "A deleted path must not remain in the after-document set.",
                    change.RepositoryPath));
            }
        }
        else if (after.Length != 1
                 || !after[0].Content.AsSpan().SequenceEqual(change.Content.AsSpan()))
        {
            diagnostics.Add(new(
                "GH1012",
                "Changed file content does not exactly match the validated after-document set.",
                change.RepositoryPath));
        }

        if (change.ExpectedState != ExpectedFileState.Present)
        {
            return;
        }

        RawManifestDocument? before = plan.BeforeDocuments.SingleOrDefault(document =>
            string.Equals(document.RepositoryPath, change.RepositoryPath, StringComparison.Ordinal));
        if (before is null
            || !string.Equals(
                WorkflowFileChange.Hash(before.Content.AsSpan()),
                change.ExpectedSha256,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "GH1013",
                "Existing-file precondition does not match the validated before-document set.",
                change.RepositoryPath));
        }
    }

    private static string? GetVersionDirectory(string path)
    {
        int lastSeparator = path.LastIndexOf('/');
        return lastSeparator <= 0 ? null : path[..lastSeparator];
    }
}
