namespace WinMatsch.Rules;

internal static class HumanCorrectionDetector
{
    public static void Detect(ManifestContext context)
    {
        if (context.OriginalBotSubmission is not { } original
            || context.Previous is not { } merged)
        {
            return;
        }

        if (!ManifestSnapshot.TryCapture(original, out ManifestSnapshot botSnapshot)
            || !ManifestSnapshot.TryCapture(merged, out ManifestSnapshot humanSnapshot)
            || !ManifestSnapshot.TryCapture(context.Manifests, out ManifestSnapshot generatedSnapshot))
        {
            return;
        }

        IReadOnlyList<RawManifestChange> humanChanges = botSnapshot.Diff(humanSnapshot);
        Dictionary<SemanticChangeKey, RawManifestChange> generatedChanges = botSnapshot
            .Diff(generatedSnapshot)
            .ToDictionary(
                static change => new SemanticChangeKey(change.DocumentKey, change.SemanticPath),
                static change => change);

        foreach (RawManifestChange humanChange in humanChanges)
        {
            var key = new SemanticChangeKey(humanChange.DocumentKey, humanChange.SemanticPath);
            generatedChanges.TryGetValue(key, out RawManifestChange? generatedChange);
            string? generatedValue = generatedChange?.After ?? humanChange.Before;

            if (!string.Equals(humanChange.Before, humanChange.After, StringComparison.Ordinal)
                && string.Equals(humanChange.Before, generatedValue, StringComparison.Ordinal))
            {
                context.AddHumanCorrectionReview(new(
                    generatedChange?.ManifestPath ?? humanChange.ManifestPath,
                    generatedChange?.FieldPath ?? humanChange.FieldPath,
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, humanChange.Before),
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, humanChange.After),
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, generatedValue)));
            }
        }
    }

    private readonly record struct SemanticChangeKey(string DocumentKey, string SemanticPath);
}
