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

        IReadOnlyList<RawManifestChange> humanChangeList = botSnapshot.Diff(humanSnapshot);
        RawManifestChange[] humanChanges = humanChangeList
            .Where(static change => !change.IsPairing)
            .ToArray();
        IReadOnlyList<RawManifestChange> generatedChangeList = botSnapshot.Diff(generatedSnapshot);
        GeneratedChangeIndex generatedChanges = GeneratedChangeIndex.Create(generatedChangeList);
        RawManifestChange?[] matchedGeneratedChanges = generatedChanges.Match(humanChanges);

        for (int humanIndex = 0; humanIndex < humanChanges.Length; humanIndex++)
        {
            RawManifestChange humanChange = humanChanges[humanIndex];
            RawManifestChange? generatedChange = matchedGeneratedChanges[humanIndex];
            string? botValue = botSnapshot.TryGetEffectiveInstallerValue(
                humanChange.SemanticPath,
                out string? effectiveBotValue)
                ? effectiveBotValue
                : humanChange.Before;
            bool humanEffectiveResolved = humanSnapshot.TryGetEffectiveInstallerValueFromPairing(
                humanChange.SemanticPath,
                humanChangeList,
                out string? effectiveHumanValue);
            if (!humanEffectiveResolved)
            {
                humanEffectiveResolved = humanSnapshot.TryGetEffectiveInstallerValue(
                    humanChange.SemanticPath,
                    out effectiveHumanValue);
            }

            string? humanValue = humanChange.After is null || humanChange.Before is null
                ? humanChange.After
                : humanEffectiveResolved ? effectiveHumanValue : humanChange.After;
            bool generatedEffectiveResolved = generatedSnapshot.TryGetEffectiveInstallerValueFromPairing(
                humanChange.SemanticPath,
                generatedChangeList,
                out string? effectiveValue);
            if (!generatedEffectiveResolved)
            {
                generatedEffectiveResolved = generatedSnapshot.TryGetEffectiveInstallerValue(
                    humanChange.SemanticPath,
                    out effectiveValue);
            }
            bool effectiveInstallerPath = ManifestSnapshot.IsEffectiveInstallerPath(humanChange.SemanticPath);
            bool rootInstallerPath = effectiveInstallerPath
                && !humanChange.SemanticPath.StartsWith(
                    "Installers{installer:",
                    StringComparison.Ordinal);
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
            bool generatedRestoresBot;
            if (installerSpecificPath)
            {
                if (generatedEffectiveResolved)
                {
                    generatedRestoresBot = ManifestSnapshot.SemanticValueEquals(
                        humanChange.FieldPath,
                        botValue,
                        effectiveValue);
                }
                else if (generatedChange is not null)
                {
                    matchedGeneratedValue = generatedChange.After;
                    generatedRestoresBot = ManifestSnapshot.SemanticValueEquals(
                        humanChange.FieldPath,
                        botValue,
                        generatedChange.After);
                }
                else
                {
                    generatedRestoresBot = generatedSnapshot.TryFindEffectiveInstallerValue(
                        humanChange.SemanticPath,
                        botValue,
                        out matchedGeneratedValue);
                }
            }
            else if (rootInstallerPath)
            {
                generatedRestoresBot = generatedSnapshot.TryFindChangedEffectiveInstallerValue(
                    humanChange.SemanticPath,
                    botValue,
                    humanSnapshot,
                    out matchedGeneratedValue);
            }
            else
            {
                generatedRestoresBot = generatedSnapshot.TryFindEffectiveInstallerValue(
                    humanChange.SemanticPath,
                    botValue,
                    out matchedGeneratedValue);
            }
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
                    RuleLogSanitizer.Sanitize(humanChange.FieldPath, reviewGeneratedValue),
                    humanChange.DocumentKey,
                    humanChange.SemanticPath,
                    CorrectionFingerprint(
                        humanChange.DocumentKey,
                        humanChange.SemanticPath,
                        botValue,
                        humanValue,
                        reviewGeneratedValue)));
            }
        }
    }

    private static string CorrectionFingerprint(
        string documentKey,
        string semanticPath,
        string? botValue,
        string? humanValue,
        string? generatedValue)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(
                '\u001f',
                documentKey,
                semanticPath,
                botValue ?? "<null>",
                humanValue ?? "<null>",
                generatedValue ?? "<null>"))));

    private readonly record struct SemanticChangeKey(string DocumentKey, string SemanticPath);

    private readonly record struct SemanticChangeShapeKey(string DocumentKey, string SemanticPath);

    private sealed class GeneratedChangeIndex
    {
        private readonly Dictionary<SemanticChangeKey, Queue<IndexedChange>> _exact;
        private readonly Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>> _shape;
        private readonly Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>> _broadShape;

        private GeneratedChangeIndex(
            Dictionary<SemanticChangeKey, Queue<IndexedChange>> exact,
            Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>> shape,
            Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>> broadShape)
        {
            _exact = exact;
            _shape = shape;
            _broadShape = broadShape;
        }

        public static GeneratedChangeIndex Create(IReadOnlyList<RawManifestChange> changes)
        {
            var exact = new Dictionary<SemanticChangeKey, Queue<IndexedChange>>();
            var shape = new Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>>();
            var broadShape = new Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>>();
            foreach (RawManifestChange change in changes.Where(static change => !change.IsPairing))
            {
                var indexed = new IndexedChange(change);
                Enqueue(
                    exact,
                    new(change.DocumentKey, change.SemanticPath),
                    indexed);
                Enqueue(
                    shape,
                    new(
                        RemoveDocumentOccurrence(change.DocumentKey),
                        RemoveInstallerOccurrence(change.SemanticPath)),
                    indexed);
                Enqueue(
                    broadShape,
                    new(
                        RemoveDocumentShape(change.DocumentKey),
                        RemoveInstallerShape(change.SemanticPath)),
                    indexed);
            }

            return new(exact, shape, broadShape);
        }

        public RawManifestChange?[] Match(RawManifestChange[] humanChanges)
        {
            var matches = new RawManifestChange?[humanChanges.Length];
            for (int i = 0; i < humanChanges.Length; i++)
            {
                RawManifestChange humanChange = humanChanges[i];
                var exactKey = new SemanticChangeKey(
                    humanChange.DocumentKey,
                    humanChange.SemanticPath);
                if (TryDequeue(_exact.GetValueOrDefault(exactKey), out RawManifestChange? match))
                {
                    matches[i] = match;
                }
            }

            MatchRemaining(humanChanges, matches, _shape, broad: false);
            MatchRemaining(humanChanges, matches, _broadShape, broad: true);
            return matches;
        }

        private static void MatchRemaining(
            RawManifestChange[] humanChanges,
            RawManifestChange?[] matches,
            Dictionary<SemanticChangeShapeKey, Queue<IndexedChange>> index,
            bool broad)
        {
            for (int i = 0; i < humanChanges.Length; i++)
            {
                if (matches[i] is not null)
                {
                    continue;
                }

                RawManifestChange humanChange = humanChanges[i];
                var key = new SemanticChangeShapeKey(
                    broad
                        ? RemoveDocumentShape(humanChange.DocumentKey)
                        : RemoveDocumentOccurrence(humanChange.DocumentKey),
                    broad
                        ? RemoveInstallerShape(humanChange.SemanticPath)
                        : RemoveInstallerOccurrence(humanChange.SemanticPath));
                if (TryDequeue(index.GetValueOrDefault(key), out RawManifestChange? match))
                {
                    matches[i] = match;
                }
            }
        }

        private static void Enqueue<TKey>(
            Dictionary<TKey, Queue<IndexedChange>> index,
            TKey key,
            IndexedChange change)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out Queue<IndexedChange>? bucket))
            {
                bucket = [];
                index.Add(key, bucket);
            }

            bucket.Enqueue(change);
        }

        private static bool TryDequeue(
            Queue<IndexedChange>? bucket,
            out RawManifestChange? change)
        {
            while (bucket is { Count: > 0 })
            {
                IndexedChange candidate = bucket.Dequeue();
                if (candidate.Consumed)
                {
                    continue;
                }

                candidate.Consumed = true;
                change = candidate.Change;
                return true;
            }

            change = null;
            return false;
        }

        private static string RemoveInstallerOccurrence(string semanticPath)
        {
            const string marker = "{installer:";
            int markerStart = semanticPath.IndexOf(marker, StringComparison.Ordinal);
            if (markerStart < 0)
            {
                return semanticPath;
            }

            int close = semanticPath.IndexOf('}', markerStart + marker.Length);
            int occurrence = close < 0
                ? -1
                : semanticPath.LastIndexOf('#', close - 1, close - markerStart);
            return occurrence < 0
                ? semanticPath
                : string.Concat(semanticPath.AsSpan(0, occurrence), "#*", semanticPath.AsSpan(close));
        }

        private static string RemoveDocumentOccurrence(string documentKey)
        {
            int added = documentKey.IndexOf("#added:", StringComparison.Ordinal);
            int occurrence = documentKey.LastIndexOf('#');
            return added < 0 || occurrence <= added + 7
                ? documentKey
                : string.Concat(documentKey.AsSpan(0, occurrence), "#*");
        }

        private static string RemoveInstallerShape(string semanticPath)
        {
            string normalized = RemoveInstallerOccurrence(semanticPath);
            const string marker = "{installer:";
            int markerStart = normalized.IndexOf(marker, StringComparison.Ordinal);
            int close = markerStart < 0 ? -1 : normalized.IndexOf('}', markerStart + marker.Length);
            int shape = close < 0 ? -1 : normalized.LastIndexOf('+', close - 1, close - markerStart);
            int occurrence = close < 0 ? -1 : normalized.LastIndexOf('#', close - 1, close - markerStart);
            return shape < 0 || occurrence < shape
                ? normalized
                : string.Concat(normalized.AsSpan(0, shape), normalized.AsSpan(occurrence));
        }

        private static string RemoveDocumentShape(string documentKey)
        {
            int added = documentKey.IndexOf("#added:", StringComparison.Ordinal);
            return added < 0
                ? documentKey
                : string.Concat(documentKey.AsSpan(0, added), "#added:*");
        }

        private sealed class IndexedChange(RawManifestChange change)
        {
            public RawManifestChange Change { get; } = change;

            public bool Consumed { get; set; }
        }
    }
}
