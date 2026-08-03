using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Rules.OverridePacks;

public static class LearnedOverrideBuilder
{
    public static ImmutableArray<LearnedFieldOverride> Create(
        PackageManifests originalBotSubmission,
        PackageManifests mergedManifest,
        IEnumerable<HumanCorrectionReview> approvedReviews)
    {
        ArgumentNullException.ThrowIfNull(originalBotSubmission);
        ArgumentNullException.ThrowIfNull(mergedManifest);
        ArgumentNullException.ThrowIfNull(approvedReviews);
        if (!ManifestSnapshot.TryCapture(originalBotSubmission, out ManifestSnapshot original)
            || !ManifestSnapshot.TryCapture(mergedManifest, out ManifestSnapshot merged))
        {
            throw new InvalidOperationException("Approved corrections could not be snapshotted safely.");
        }

        RawManifestChange[] humanChanges =
        [
            .. original.Diff(merged).Where(static change => !change.IsPairing),
        ];
        var learned = ImmutableArray.CreateBuilder<LearnedFieldOverride>();
        foreach (HumanCorrectionReview review in approvedReviews)
        {
            if (review.DocumentKey is null
                || review.SemanticPath is null
                || review.CorrectionFingerprint is null)
            {
                throw new InvalidOperationException(
                    $"Correction '{review.FieldPath}' lacks a stable reviewed identity and cannot be persisted.");
            }

            RawManifestChange[] matches = humanChanges
                .Where(change => string.Equals(change.DocumentKey, review.DocumentKey, StringComparison.Ordinal)
                    && string.Equals(change.SemanticPath, review.SemanticPath, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1 || matches[0].After is null)
            {
                throw new InvalidOperationException(
                    $"Correction '{review.SemanticPath}' is ambiguous or represents a deletion and cannot be learned safely.");
            }

            string humanValue = matches[0].After!;
            string field = matches[0].SemanticPath.Split('.').Last();
            string? installerSelector = null;
            string? botValue = matches[0].Before;
            if (matches[0].DocumentKey == "installer"
                && TryInstallerIndex(matches[0].FieldPath, out int installerIndex))
            {
                installerSelector = LearnedInstallerSelector.Create(
                    mergedManifest,
                    installerIndex,
                    field);
                int?[] originalByMerged = ManifestSnapshot.MatchInstallerIndices(
                    originalBotSubmission,
                    mergedManifest);
                int? originalIndex = installerIndex < originalByMerged.Length
                    ? originalByMerged[installerIndex]
                    : null;
                if (originalBotSubmission.Installer.Installers is { } originalInstallers
                    && originalIndex is int pairedIndex
                    && pairedIndex >= 0
                    && pairedIndex < originalInstallers.Count)
                {
                    botValue = LearnedInstallerSelector.GetValue(
                        originalBotSubmission.Installer,
                        originalInstallers[pairedIndex],
                        field);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Correction '{review.SemanticPath}' could not resolve a unique original installer identity.");
                }
            }

            var value = new LearnedFieldOverride
            {
                DocumentKey = review.DocumentKey,
                SemanticPath = review.SemanticPath,
                Value = humanValue,
                ValueSha256 = Hash(humanValue),
                BotValueSha256 = Hash(botValue ?? "<null>"),
                SourceFingerprint = review.CorrectionFingerprint,
                Source = $"{review.ManifestPath}:{review.FieldPath}",
                InstallerSelectorSha256 = installerSelector,
            };
            OverridePackFieldSelector.ValidateLearned(value);
            learned.Add(value);
        }

        return
        [
            .. learned
                .GroupBy(
                    static item => $"{item.DocumentKey}\u001f{item.SemanticPath}",
                    StringComparer.Ordinal)
                .Select(static group => group.Single())
                .OrderBy(static item => item.DocumentKey, StringComparer.Ordinal)
                .ThenBy(static item => item.SemanticPath, StringComparer.Ordinal),
        ];
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool TryInstallerIndex(string fieldPath, out int index)
    {
        index = -1;
        const string prefix = "Installers[";
        if (!fieldPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int close = fieldPath.IndexOf(']', prefix.Length);
        return close > prefix.Length
            && int.TryParse(fieldPath.AsSpan(prefix.Length, close - prefix.Length), out index);
    }
}
