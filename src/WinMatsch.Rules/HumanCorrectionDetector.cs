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

        IReadOnlyDictionary<ManifestFieldKey, ManifestFieldValue> botValues = botSnapshot.Flatten();
        IReadOnlyDictionary<ManifestFieldKey, ManifestFieldValue> humanValues = humanSnapshot.Flatten();
        IReadOnlyDictionary<ManifestFieldKey, ManifestFieldValue> generatedValues = generatedSnapshot.Flatten();

        var paths = new SortedSet<ManifestFieldKey>(
            botValues.Keys,
            Comparer<ManifestFieldKey>.Create(static (left, right) =>
            {
                int manifest = StringComparer.Ordinal.Compare(left.DocumentKey, right.DocumentKey);
                return manifest != 0
                    ? manifest
                    : StringComparer.Ordinal.Compare(left.FieldPath, right.FieldPath);
            }));
        paths.UnionWith(humanValues.Keys);
        paths.UnionWith(generatedValues.Keys);

        foreach (ManifestFieldKey path in paths)
        {
            botValues.TryGetValue(path, out ManifestFieldValue? bot);
            humanValues.TryGetValue(path, out ManifestFieldValue? human);
            generatedValues.TryGetValue(path, out ManifestFieldValue? generated);

            if (!string.Equals(bot?.Value, human?.Value, StringComparison.Ordinal)
                && string.Equals(bot?.Value, generated?.Value, StringComparison.Ordinal))
            {
                context.AddHumanCorrectionReview(new(
                    generated?.ManifestPath ?? human?.ManifestPath ?? bot!.ManifestPath,
                    path.FieldPath,
                    RuleLogSanitizer.Sanitize(path.FieldPath, bot?.Value),
                    RuleLogSanitizer.Sanitize(path.FieldPath, human?.Value),
                    RuleLogSanitizer.Sanitize(path.FieldPath, generated?.Value)));
            }
        }
    }
}
