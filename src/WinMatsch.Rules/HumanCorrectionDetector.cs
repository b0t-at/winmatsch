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
            string? botValue = botSnapshot.TryGetEffectiveInstallerValue(
                humanChange.SemanticPath,
                out string? effectiveBotValue)
                ? effectiveBotValue
                : humanChange.Before;
            string? humanValue = humanSnapshot.TryGetEffectiveInstallerValue(
                humanChange.SemanticPath,
                out string? effectiveHumanValue)
                ? effectiveHumanValue
                : humanChange.After;
            string? generatedValue = generatedSnapshot.TryGetEffectiveInstallerValue(
                humanChange.SemanticPath,
                out string? effectiveValue)
                ? effectiveValue
                : generatedChange?.After ?? botValue;
            bool generatedRestoresBot = generatedSnapshot.TryFindEffectiveInstallerValue(
                humanChange.SemanticPath,
                botValue,
                out string? matchedGeneratedValue);

            if (!string.Equals(botValue, humanValue, StringComparison.Ordinal)
                && (generatedRestoresBot
                    || ManifestSnapshot.SemanticValueEquals(
                        humanChange.FieldPath,
                        botValue,
                        generatedValue)))
            {
                string? reviewGeneratedValue = generatedRestoresBot
                    ? matchedGeneratedValue
                    : generatedValue;
                context.AddHumanCorrectionReview(new(
                    generatedChange?.ManifestPath ?? humanChange.ManifestPath,
                    generatedChange?.FieldPath ?? humanChange.FieldPath,
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, botValue),
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, humanValue),
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, reviewGeneratedValue)));
            }
        }
    }

    private readonly record struct SemanticChangeKey(string DocumentKey, string SemanticPath);
}
