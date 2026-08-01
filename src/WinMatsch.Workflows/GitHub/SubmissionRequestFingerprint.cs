using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinMatsch.Workflows.GitHub;

internal static class SubmissionRequestFingerprint
{
    public const int CurrentVersion = 1;

    public static string Create(SubmissionJournalRemoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add("format", CurrentVersion.ToString(CultureInfo.InvariantCulture));
        Add("upstream", request.UpstreamRepository.ToString());
        Add("target", request.TargetRepository?.ToString());
        Add("fork-owner", request.ForkOwner);
        Add("operation", request.Operation.ToString());
        Add("fork-consent", request.Policy.ForkConsent.ToString());
        Add("skip-pr-check", request.Policy.SkipPullRequestCheck ? "true" : "false");
        Add("replace", request.Policy.ReplacePreviousVersion ? "true" : "false");
        Add("previous-version", request.Policy.PreviousVersion?.Value);
        Add(
            "freshness-ticks",
            request.Policy.MinimumReleaseFreshness.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (string denied in request.Policy.DuplicateHashes.DeniedSha256
                     .Select(static value => value.ToUpperInvariant())
                     .Order(StringComparer.Ordinal))
        {
            Add("denied-sha256", denied);
        }

        foreach (string allowed in request.Policy.DuplicateHashes.AllowedSha256
                     .Select(static value => value.ToUpperInvariant())
                     .Order(StringComparer.Ordinal))
        {
            Add("allowed-sha256", allowed);
        }

        Add(
            "duplicate-override",
            request.Policy.DuplicateHashes.OverrideAnnotation);
        Add("created-with", request.CreatedWith);
        Add("custom-title", request.CustomTitle);
        Add("resolves", request.Resolves);
        Add(
            "supersedes",
            request.SupersedesPullRequestNumber?.ToString(CultureInfo.InvariantCulture));
        Add("idempotency", request.IdempotencyKey);
        foreach (RepositoryInstallerEvidence evidence in request.RepositoryEvidence
                     .OrderBy(static item => item.PackageIdentifier.Value, StringComparer.Ordinal)
                     .ThenBy(static item => item.PackageVersion.Value, StringComparer.Ordinal)
                     .ThenBy(static item => item.ManifestPath, StringComparer.Ordinal)
                     .ThenBy(static item => item.InstallerSha256, StringComparer.OrdinalIgnoreCase))
        {
            Add("evidence-package", evidence.PackageIdentifier.Value);
            Add("evidence-version", evidence.PackageVersion.Value);
            Add("evidence-sha256", evidence.InstallerSha256.ToUpperInvariant());
            Add("evidence-path", evidence.ManifestPath);
            Add("evidence-retired", evidence.RetiredIdentifier ? "true" : "false");
        }

        foreach (string annotation in request.VanityUrlAnnotations.Order(StringComparer.Ordinal))
        {
            Add("vanity-annotation", annotation);
        }

        Add(
            "release-updated",
            request.ReleaseUpdatedAt?.ToUniversalTime().ToString("O"));
        Add("release-repository", request.ReleaseRepository?.ToString());
        Add("release-id", request.ReleaseId?.ToString(CultureInfo.InvariantCulture));
        Add("commit-title", request.Presentation.CommitTitle);
        Add("pr-title", request.Presentation.PullRequestTitle);
        Add("pr-body", request.Presentation.PullRequestBody);
        return Convert.ToHexString(hash.GetHashAndReset());

        void Add(string label, string? value)
        {
            Append(label);
            if (value is null)
            {
                AppendLength(-1);
                return;
            }

            Append(value);
        }

        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            AppendLength(bytes.Length);
            hash.AppendData(bytes);
        }

        void AppendLength(int value)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, value);
            hash.AppendData(length);
        }
    }
}
