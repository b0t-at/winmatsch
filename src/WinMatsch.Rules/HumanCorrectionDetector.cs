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

        IReadOnlyList<RawManifestChange> humanChanges = botSnapshot
            .Diff(humanSnapshot)
            .Where(static change => !change.IsPairing)
            .ToArray();
        IReadOnlyList<RawManifestChange> generatedChangeList = botSnapshot.Diff(generatedSnapshot);
        IReadOnlyList<RawManifestChange> humanGeneratedChanges = humanSnapshot
            .Diff(generatedSnapshot)
            .Where(static change => !change.IsPairing)
            .ToArray();
        Dictionary<SemanticChangeKey, RawManifestChange> generatedChanges = generatedChangeList
            .Where(static change => !change.IsPairing)
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
            bool generatedEffectiveResolved = generatedSnapshot.TryGetEffectiveInstallerValue(
                humanChange.SemanticPath,
                out string? effectiveValue);
            if (!generatedEffectiveResolved)
            {
                generatedEffectiveResolved = generatedSnapshot.TryGetEffectiveInstallerValueFromPairing(
                    humanChange.SemanticPath,
                    generatedChangeList,
                    out effectiveValue);
            }
            bool effectiveInstallerPath = ManifestSnapshot.IsEffectiveInstallerPath(humanChange.SemanticPath);
            bool rootInstallerPath = effectiveInstallerPath
                && !humanChange.SemanticPath.StartsWith(
                    "Installers{installer:",
                    StringComparison.Ordinal);
            if (rootInstallerPath
                && !HasRelatedEffectiveChange(humanGeneratedChanges, humanChange.SemanticPath))
            {
                continue;
            }

            bool generatedValueKnown = generatedEffectiveResolved
                || generatedChange is not null
                || !effectiveInstallerPath;
            string? generatedValue = generatedEffectiveResolved
                ? effectiveValue
                : generatedChange is null ? botValue : generatedChange.After;
            string? matchedGeneratedValue = null;
            bool installerSpecificPath = humanChange.SemanticPath.StartsWith(
                "Installers{installer:",
                StringComparison.Ordinal);
            bool generatedRestoresBot = generatedEffectiveResolved && installerSpecificPath
                ? ManifestSnapshot.SemanticValueEquals(
                    humanChange.FieldPath,
                    botValue,
                    effectiveValue)
                : generatedSnapshot.TryFindEffectiveInstallerValue(
                    humanChange.SemanticPath,
                    botValue,
                    out matchedGeneratedValue);
            if (generatedEffectiveResolved && installerSpecificPath && generatedRestoresBot)
            {
                matchedGeneratedValue = effectiveValue;
            }

            if (!string.Equals(botValue, humanValue, StringComparison.Ordinal)
                && (generatedRestoresBot
                    || generatedValueKnown && ManifestSnapshot.SemanticValueEquals(
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

    private static bool HasRelatedEffectiveChange(
        IReadOnlyList<RawManifestChange> changes,
        string rootSemanticPath)
    {
        return changes.Any(
            change => change.DocumentKey == "installer"
                && (string.Equals(change.SemanticPath, rootSemanticPath, StringComparison.Ordinal)
                    || IsInstallerChangeForRootPath(change.SemanticPath, rootSemanticPath)));
    }

    private static bool IsInstallerChangeForRootPath(string semanticPath, string rootSemanticPath)
    {
        if (!semanticPath.StartsWith("Installers{installer:", StringComparison.Ordinal))
        {
            return false;
        }

        int close = semanticPath.IndexOf('}');
        return close >= 0
            && close + 2 <= semanticPath.Length
            && string.Equals(
                semanticPath[(close + 2)..],
                rootSemanticPath,
                StringComparison.Ordinal);
    }

    private readonly record struct SemanticChangeKey(string DocumentKey, string SemanticPath);
}
