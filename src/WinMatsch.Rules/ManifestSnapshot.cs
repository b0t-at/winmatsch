using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Rules;

internal sealed class ManifestSnapshot
{
    private const int InstallerMatchThreshold = 150;
    private const int MaximumLcsCells = 1_000_000;
    private const int MaximumInstallerMatchComparisons = 1_000_000;
    private static readonly object _installerSerializationLock = new();
    private static readonly Sha256Hash _missingHashPlaceholder = new(new string('0', Sha256Hash.Length));
    private readonly IReadOnlyDictionary<string, DocumentSnapshot> _documents;

    private ManifestSnapshot(IReadOnlyDictionary<string, DocumentSnapshot> documents)
    {
        _documents = documents;
    }

    public static ManifestSnapshot Capture(PackageManifests manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var documents = new SortedDictionary<string, DocumentSnapshot>(StringComparer.Ordinal);

        Add(documents, "version", GetVersionPath(manifests), ManifestYamlWriter.Serialize(manifests.Version));
        Add(documents, "installer", GetInstallerPath(manifests), SerializeInstaller(manifests.Installer, out _));
        Add(
            documents,
            "defaultLocale",
            GetDefaultLocalePath(manifests),
            ManifestYamlWriter.Serialize(manifests.DefaultLocale));

        for (int i = 0; i < manifests.Locales.Count; i++)
        {
            LocaleManifest locale = manifests.Locales[i];
            string documentKey = locale.PackageLocale is { } packageLocale
                ? $"locale:{packageLocale.Value.ToUpperInvariant()}"
                : $"locale:missing:{i}";
            Add(documents, documentKey, GetLocalePath(manifests, locale, i), ManifestYamlWriter.Serialize(locale));
        }

        return new(documents);
    }

    public static PackageManifests Clone(PackageManifests manifests)
    {
        return ManifestClone.Clone(manifests);
    }

    public static bool TryCapture(PackageManifests manifests, out ManifestSnapshot snapshot)
    {
        try
        {
            snapshot = Capture(manifests);
            return true;
        }
        catch (InvalidOperationException)
        {
            try
            {
                snapshot = Capture(ManifestClone.CreateSerializable(manifests));
                return true;
            }
            catch (InvalidOperationException)
            {
                snapshot = null!;
                return false;
            }
        }
    }

    public IReadOnlyList<RawManifestChange> Diff(ManifestSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<RawManifestChange>();
        var documentKeys = new SortedSet<string>(_documents.Keys, StringComparer.Ordinal);
        documentKeys.UnionWith(after._documents.Keys);

        foreach (string documentKey in documentKeys)
        {
            _documents.TryGetValue(documentKey, out DocumentSnapshot? beforeDocument);
            after._documents.TryGetValue(documentKey, out DocumentSnapshot? afterDocument);
            DiffNode(
                documentKey,
                afterDocument?.ManifestPath ?? beforeDocument!.ManifestPath,
                fieldPath: string.Empty,
                semanticPath: string.Empty,
                beforeDocument?.Root,
                afterDocument?.Root,
                changes);
        }

        return changes;
    }

    public static string GetInstallerPath(PackageManifests manifests)
        => manifests.Installer.PackageIdentifier is { } identifier
            ? ManifestPaths.GetInstallerFileName(identifier)
            : "installer.yaml";

    private static string GetVersionPath(PackageManifests manifests)
        => manifests.Version.PackageIdentifier is { } identifier
            ? ManifestPaths.GetVersionFileName(identifier)
            : "version.yaml";

    private static string GetDefaultLocalePath(PackageManifests manifests)
        => manifests.DefaultLocale.PackageIdentifier is { } identifier
            && manifests.DefaultLocale.PackageLocale is { } locale
            ? ManifestPaths.GetLocaleFileName(identifier, locale)
            : "defaultLocale.yaml";

    private static string GetLocalePath(PackageManifests manifests, LocaleManifest locale, int index)
        => locale.PackageIdentifier is { } identifier && locale.PackageLocale is { } language
            ? ManifestPaths.GetLocaleFileName(identifier, language)
            : $"locale[{index}].yaml";

    private static void Add(
        IDictionary<string, DocumentSnapshot> documents,
        string documentKey,
        string manifestPath,
        string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        documents.Add(documentKey, new(manifestPath, stream.Documents[0].RootNode));
    }

    private static string SerializeInstaller(InstallerManifest manifest, out int[] missingHashIndices)
    {
        List<Installer>? installers = manifest.Installers;
        if (installers is null)
        {
            missingHashIndices = [];
            return ManifestYamlWriter.Serialize(manifest);
        }

        lock (_installerSerializationLock)
        {
            var missing = new List<int>();
            for (int i = 0; i < installers.Count; i++)
            {
                if (installers[i].InstallerSha256 is null)
                {
                    installers[i].InstallerSha256 = _missingHashPlaceholder;
                    missing.Add(i);
                }
            }

            try
            {
                missingHashIndices = [.. missing];
                return ManifestYamlWriter.Serialize(manifest);
            }
            finally
            {
                foreach (int index in missing)
                {
                    installers[index].InstallerSha256 = null;
                }
            }
        }
    }

    private static void DiffNode(
        string documentKey,
        string manifestPath,
        string fieldPath,
        string semanticPath,
        YamlNode? before,
        YamlNode? after,
        ICollection<RawManifestChange> changes)
    {
        if (before is YamlMappingNode beforeMapping && after is YamlMappingNode afterMapping)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            Dictionary<string, YamlNode> beforeValues = ToMapping(beforeMapping);
            Dictionary<string, YamlNode> afterValues = ToMapping(afterMapping);
            keys.UnionWith(beforeValues.Keys);
            keys.UnionWith(afterValues.Keys);
            foreach (string key in keys)
            {
                beforeValues.TryGetValue(key, out YamlNode? beforeChild);
                afterValues.TryGetValue(key, out YamlNode? afterChild);
                DiffNode(
                    documentKey,
                    manifestPath,
                    AppendProperty(fieldPath, key),
                    AppendProperty(semanticPath, key),
                    beforeChild,
                    afterChild,
                    changes);
            }

            return;
        }

        if (before is YamlSequenceNode beforeSequence && after is YamlSequenceNode afterSequence)
        {
            if (documentKey == "installer" && fieldPath == "Installers")
            {
                DiffInstallerSequence(
                    documentKey,
                    manifestPath,
                    fieldPath,
                    semanticPath,
                    beforeSequence,
                    afterSequence,
                    changes);
            }
            else
            {
                DiffSequence(
                    documentKey,
                    manifestPath,
                    fieldPath,
                    semanticPath,
                    beforeSequence,
                    afterSequence,
                    changes);
            }

            return;
        }

        if (before is null && after is YamlMappingNode or YamlSequenceNode)
        {
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                fieldPath,
                semanticPath,
                after,
                beforeValue: null,
                adding: true,
                changes);
            return;
        }

        if (after is null && before is YamlMappingNode or YamlSequenceNode)
        {
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                fieldPath,
                semanticPath,
                before,
                beforeValue: null,
                adding: false,
                changes);
            return;
        }

        string? beforeValue = ScalarValue(before);
        string? afterValue = ScalarValue(after);
        if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal)
            || before?.NodeType != after?.NodeType)
        {
            changes.Add(new(documentKey, manifestPath, fieldPath, semanticPath, beforeValue, afterValue));
        }
    }

    private static void DiffInstallerSequence(
        string documentKey,
        string manifestPath,
        string fieldPath,
        string semanticPath,
        YamlSequenceNode before,
        YamlSequenceNode after,
        ICollection<RawManifestChange> changes)
    {
        List<SequencePair> pairs = MatchInstallerItems(before, after);
        var matchedBefore = new HashSet<int>(pairs.Select(static pair => pair.BeforeIndex));
        var matchedAfter = new HashSet<int>(pairs.Select(static pair => pair.AfterIndex));

        foreach (SequencePair pair in pairs.OrderBy(static pair => pair.AfterIndex))
        {
            string identity = InstallerIdentity(before.Children[pair.BeforeIndex]);
            int occurrence = InstallerOccurrenceAt(before, pair.BeforeIndex, identity);
            DiffNode(
                documentKey,
                manifestPath,
                $"{fieldPath}[{pair.AfterIndex}]",
                $"{semanticPath}{{installer:{Hash(identity)}#{occurrence}}}",
                before.Children[pair.BeforeIndex],
                after.Children[pair.AfterIndex],
                changes);
        }

        for (int i = 0; i < before.Children.Count; i++)
        {
            if (!matchedBefore.Contains(i))
            {
                string identity = InstallerIdentity(before.Children[i]);
                FlattenAddedOrRemoved(
                    documentKey,
                    manifestPath,
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{installer:{Hash(identity)}#{InstallerOccurrenceAt(before, i, identity)}}}",
                    before.Children[i],
                    beforeValue: null,
                    adding: false,
                    changes);
            }
        }

        for (int i = 0; i < after.Children.Count; i++)
        {
            if (!matchedAfter.Contains(i))
            {
                string identity = InstallerIdentity(after.Children[i]);
                FlattenAddedOrRemoved(
                    documentKey,
                    manifestPath,
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{installer:{Hash(identity)}#{InstallerOccurrenceAt(after, i, identity)}}}",
                    after.Children[i],
                    beforeValue: null,
                    adding: true,
                    changes);
            }
        }
    }

    private static List<SequencePair> MatchInstallerItems(
        YamlSequenceNode before,
        YamlSequenceNode after)
    {
        InstallerMatchValues[] beforeValues = [.. before.Children.Select(GetInstallerMatchValues)];
        InstallerMatchValues[] afterValues = [.. after.Children.Select(GetInstallerMatchValues)];
        var matchedBefore = new HashSet<int>();
        var matchedAfter = new HashSet<int>();
        var pairs = new List<SequencePair>();

        MatchInstallersByKey(
            beforeValues,
            afterValues,
            static value => value.PrimaryIdentity,
            matchedBefore,
            matchedAfter,
            pairs);
        MatchInstallersByKey(
            beforeValues,
            afterValues,
            static value => value.UrlPattern,
            matchedBefore,
            matchedAfter,
            pairs);

        int remainingBefore = beforeValues.Length - matchedBefore.Count;
        int remainingAfter = afterValues.Length - matchedAfter.Count;
        if ((long)remainingBefore * remainingAfter <= MaximumInstallerMatchComparisons)
        {
            for (int beforeIndex = 0; beforeIndex < beforeValues.Length; beforeIndex++)
            {
                if (matchedBefore.Contains(beforeIndex))
                {
                    continue;
                }

                int bestAfterIndex = -1;
                int bestScore = InstallerMatchThreshold - 1;
                int bestDistance = int.MaxValue;
                for (int afterIndex = 0; afterIndex < afterValues.Length; afterIndex++)
                {
                    if (matchedAfter.Contains(afterIndex))
                    {
                        continue;
                    }

                    int score = InstallerMatchScore(beforeValues[beforeIndex], afterValues[afterIndex]);
                    int distance = Math.Abs(beforeIndex - afterIndex);
                    if (score > bestScore || score == bestScore && distance < bestDistance)
                    {
                        bestAfterIndex = afterIndex;
                        bestScore = score;
                        bestDistance = distance;
                    }
                }

                if (bestAfterIndex >= 0)
                {
                    matchedBefore.Add(beforeIndex);
                    matchedAfter.Add(bestAfterIndex);
                    pairs.Add(new(beforeIndex, bestAfterIndex));
                }
            }
        }
        else
        {
            int count = Math.Min(beforeValues.Length, afterValues.Length);
            for (int index = 0; index < count; index++)
            {
                if (!matchedBefore.Contains(index)
                    && !matchedAfter.Contains(index)
                    && InstallerMatchScore(beforeValues[index], afterValues[index]) >= InstallerMatchThreshold)
                {
                    matchedBefore.Add(index);
                    matchedAfter.Add(index);
                    pairs.Add(new(index, index));
                }
            }
        }

        return pairs;
    }

    private static void MatchInstallersByKey(
        InstallerMatchValues[] before,
        InstallerMatchValues[] after,
        Func<InstallerMatchValues, string?> keySelector,
        HashSet<int> matchedBefore,
        HashSet<int> matchedAfter,
        ICollection<SequencePair> pairs)
    {
        var afterByKey = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < after.Length; index++)
        {
            if (matchedAfter.Contains(index) || string.IsNullOrEmpty(keySelector(after[index])))
            {
                continue;
            }

            string key = keySelector(after[index])!;
            if (!afterByKey.TryGetValue(key, out Queue<int>? indices))
            {
                indices = new();
                afterByKey.Add(key, indices);
            }

            indices.Enqueue(index);
        }

        for (int index = 0; index < before.Length; index++)
        {
            if (matchedBefore.Contains(index) || string.IsNullOrEmpty(keySelector(before[index])))
            {
                continue;
            }

            string key = keySelector(before[index])!;
            if (!afterByKey.TryGetValue(key, out Queue<int>? indices))
            {
                continue;
            }

            while (indices.Count > 0 && matchedAfter.Contains(indices.Peek()))
            {
                indices.Dequeue();
            }

            if (indices.Count > 0)
            {
                int afterIndex = indices.Dequeue();
                matchedBefore.Add(index);
                matchedAfter.Add(afterIndex);
                pairs.Add(new(index, afterIndex));
            }
        }
    }

    private static int InstallerMatchScore(
        InstallerMatchValues before,
        InstallerMatchValues after)
    {
        int score = 0;
        if (EqualNonEmpty(before.PrimaryIdentity, after.PrimaryIdentity))
        {
            score += 10_000;
        }

        if (EqualNonEmpty(before.UrlPattern, after.UrlPattern))
        {
            score += 1_000;
        }

        if (EqualNonEmpty(before.InstallerType, after.InstallerType))
        {
            score += 200;
        }

        if (EqualNonEmpty(before.Scope, after.Scope))
        {
            score += 100;
        }

        if (EqualNonEmpty(before.Locale, after.Locale))
        {
            score += 80;
        }

        if (EqualNonEmpty(before.NestedInstallerType, after.NestedInstallerType))
        {
            score += 60;
        }

        if (EqualNonEmpty(before.Architecture, after.Architecture))
        {
            score += 50;
        }

        if (EqualNonEmpty(before.ProductCode, after.ProductCode))
        {
            score += 40;
        }

        return score;
    }

    private static bool EqualNonEmpty(string? left, string? right)
        => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static InstallerMatchValues GetInstallerMatchValues(YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new(CanonicalNode(node), null, null, null, null, null, null, null);
        }

        Dictionary<string, YamlNode> values = ToMapping(mapping);
        string? urlPattern = GetScalar(values, "InstallerUrl") is { } url
            ? NormalizeInstallerUrl(url)
            : null;
        string? installerType = GetScalar(values, "InstallerType");
        string? scope = GetScalar(values, "Scope");
        string? locale = GetScalar(values, "InstallerLocale");
        string? nestedInstallerType = GetScalar(values, "NestedInstallerType");
        string? architecture = GetScalar(values, "Architecture");
        string? productCode = GetScalar(values, "ProductCode");
        string primary = string.Join(
            '\u001f',
            urlPattern ?? string.Empty,
            installerType ?? string.Empty,
            scope ?? string.Empty,
            locale ?? string.Empty,
            nestedInstallerType ?? string.Empty);
        return new(
            primary,
            urlPattern,
            installerType,
            scope,
            locale,
            nestedInstallerType,
            architecture,
            productCode);
    }

    private static string InstallerIdentity(YamlNode node)
    {
        InstallerMatchValues values = GetInstallerMatchValues(node);
        return string.IsNullOrEmpty(values.PrimaryIdentity)
            ? CanonicalNode(node)
            : values.PrimaryIdentity;
    }

    private static string? GetScalar(Dictionary<string, YamlNode> values, string key)
        => values.TryGetValue(key, out YamlNode? node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string NormalizeInstallerUrl(string value)
    {
        string path = Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : value.Split(['?', '#'], 2)[0];
        var normalized = new StringBuilder(path.Length);
        for (int i = 0; i < path.Length;)
        {
            if (!char.IsAsciiDigit(path[i]))
            {
                normalized.Append(char.ToLowerInvariant(path[i]));
                i++;
                continue;
            }

            int start = i;
            while (i < path.Length && char.IsAsciiDigit(path[i]))
            {
                i++;
            }

            ReadOnlySpan<char> digits = path.AsSpan(start, i - start);
            bool architectureToken = start > 0
                && char.ToLowerInvariant(path[start - 1]) == 'x'
                && (digits.SequenceEqual("64") || digits.SequenceEqual("86"));
            normalized.Append(architectureToken ? digits : "#");
        }

        return normalized.ToString();
    }

    private static int InstallerOccurrenceAt(YamlSequenceNode sequence, int index, string identity)
    {
        int occurrence = 0;
        for (int i = 0; i < index; i++)
        {
            if (string.Equals(InstallerIdentity(sequence.Children[i]), identity, StringComparison.Ordinal))
            {
                occurrence++;
            }
        }

        return occurrence;
    }

    private static void DiffSequence(
        string documentKey,
        string manifestPath,
        string fieldPath,
        string semanticPath,
        YamlSequenceNode before,
        YamlSequenceNode after,
        ICollection<RawManifestChange> changes)
    {
        string[] beforeValues = [.. before.Children.Select(CanonicalNode)];
        string[] afterValues = [.. after.Children.Select(CanonicalNode)];
        if (beforeValues.AsSpan().SequenceEqual(afterValues))
        {
            return;
        }

        List<SequencePair> anchors;
        if ((long)beforeValues.Length * afterValues.Length <= MaximumLcsCells)
        {
            int[,] lengths = BuildLcsLengths(beforeValues, afterValues);
            anchors = GetLcsPairs(beforeValues, afterValues, lengths);
        }
        else
        {
            anchors = GetLinearSubsequencePairs(beforeValues, afterValues);
        }

        int beforeStart = 0;
        int afterStart = 0;
        foreach (SequencePair anchor in anchors.Append(new(before.Children.Count, after.Children.Count)))
        {
            DiffUnmatchedSequenceRange(
                documentKey,
                manifestPath,
                fieldPath,
                semanticPath,
                before,
                after,
                beforeStart,
                anchor.BeforeIndex,
                afterStart,
                anchor.AfterIndex,
                changes);

            if (anchor.BeforeIndex < before.Children.Count)
            {
                string identity = beforeValues[anchor.BeforeIndex];
                int occurrence = OccurrenceAt(beforeValues, anchor.BeforeIndex, identity);
                DiffNode(
                    documentKey,
                    manifestPath,
                    $"{fieldPath}[{anchor.AfterIndex}]",
                    $"{semanticPath}{{item:{Hash(identity)}#{occurrence}}}",
                    before.Children[anchor.BeforeIndex],
                    after.Children[anchor.AfterIndex],
                    changes);
            }

            beforeStart = anchor.BeforeIndex + 1;
            afterStart = anchor.AfterIndex + 1;
        }
    }

    private static void DiffUnmatchedSequenceRange(
        string documentKey,
        string manifestPath,
        string fieldPath,
        string semanticPath,
        YamlSequenceNode before,
        YamlSequenceNode after,
        int beforeStart,
        int beforeEnd,
        int afterStart,
        int afterEnd,
        ICollection<RawManifestChange> changes)
    {
        int paired = Math.Min(beforeEnd - beforeStart, afterEnd - afterStart);
        for (int offset = 0; offset < paired; offset++)
        {
            int beforeIndex = beforeStart + offset;
            int afterIndex = afterStart + offset;
            string identity = CanonicalNode(before.Children[beforeIndex]);
            DiffNode(
                documentKey,
                manifestPath,
                $"{fieldPath}[{afterIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{SequenceOccurrenceAt(before, beforeIndex, identity)}}}",
                before.Children[beforeIndex],
                after.Children[afterIndex],
                changes);
        }

        for (int beforeIndex = beforeStart + paired; beforeIndex < beforeEnd; beforeIndex++)
        {
            string identity = CanonicalNode(before.Children[beforeIndex]);
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                $"{fieldPath}[{beforeIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{SequenceOccurrenceAt(before, beforeIndex, identity)}}}",
                before.Children[beforeIndex],
                beforeValue: null,
                adding: false,
                changes);
        }

        for (int afterIndex = afterStart + paired; afterIndex < afterEnd; afterIndex++)
        {
            string identity = CanonicalNode(after.Children[afterIndex]);
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                $"{fieldPath}[{afterIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{SequenceOccurrenceAt(after, afterIndex, identity)}}}",
                after.Children[afterIndex],
                beforeValue: null,
                adding: true,
                changes);
        }
    }

    private static int[,] BuildLcsLengths(string[] before, string[] after)
    {
        var lengths = new int[before.Length + 1, after.Length + 1];
        for (int i = before.Length - 1; i >= 0; i--)
        {
            for (int j = after.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        return lengths;
    }

    private static List<SequencePair> GetLcsPairs(
        string[] before,
        string[] after,
        int[,] lengths)
    {
        var pairs = new List<SequencePair>();
        int i = 0;
        int j = 0;
        while (i < before.Length && j < after.Length)
        {
            if (string.Equals(before[i], after[j], StringComparison.Ordinal))
            {
                pairs.Add(new(i, j));
                i++;
                j++;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return pairs;
    }

    private static List<SequencePair> GetLinearSubsequencePairs(string[] before, string[] after)
    {
        var afterIndices = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int index = 0; index < after.Length; index++)
        {
            if (!afterIndices.TryGetValue(after[index], out Queue<int>? indices))
            {
                indices = new();
                afterIndices.Add(after[index], indices);
            }

            indices.Enqueue(index);
        }

        var pairs = new List<SequencePair>();
        int lastAfterIndex = -1;
        for (int beforeIndex = 0; beforeIndex < before.Length; beforeIndex++)
        {
            if (!afterIndices.TryGetValue(before[beforeIndex], out Queue<int>? indices))
            {
                continue;
            }

            while (indices.Count > 0 && indices.Peek() <= lastAfterIndex)
            {
                indices.Dequeue();
            }

            if (indices.Count > 0)
            {
                int afterIndex = indices.Dequeue();
                pairs.Add(new(beforeIndex, afterIndex));
                lastAfterIndex = afterIndex;
            }
        }

        return pairs;
    }

    private static int OccurrenceAt(string[] values, int index, string identity)
    {
        int occurrence = 0;
        for (int i = 0; i < index; i++)
        {
            if (string.Equals(values[i], identity, StringComparison.Ordinal))
            {
                occurrence++;
            }
        }

        return occurrence;
    }

    private static int SequenceOccurrenceAt(YamlSequenceNode sequence, int index, string identity)
    {
        int occurrence = 0;
        for (int i = 0; i < index; i++)
        {
            if (string.Equals(CanonicalNode(sequence.Children[i]), identity, StringComparison.Ordinal))
            {
                occurrence++;
            }
        }

        return occurrence;
    }

    private static void FlattenAddedOrRemoved(
        string documentKey,
        string manifestPath,
        string fieldPath,
        string semanticPath,
        YamlNode node,
        string? beforeValue,
        bool adding,
        ICollection<RawManifestChange> changes)
    {
        var values = new List<FlattenedValue>();
        FlattenNode(fieldPath, semanticPath, node, values);
        foreach (FlattenedValue value in values.OrderBy(static value => value.SemanticPath, StringComparer.Ordinal))
        {
            changes.Add(new(
                documentKey,
                manifestPath,
                value.FieldPath,
                value.SemanticPath,
                adding ? beforeValue : value.Value,
                adding ? value.Value : beforeValue));
        }
    }

    private static void FlattenNode(
        string fieldPath,
        string semanticPath,
        YamlNode node,
        ICollection<FlattenedValue> values)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach ((string key, YamlNode value) in ToMapping(mapping).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                FlattenNode(
                    AppendProperty(fieldPath, key),
                    AppendProperty(semanticPath, key),
                    value,
                    values);
            }

            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (int i = 0; i < sequence.Children.Count; i++)
            {
                string identity = CanonicalNode(sequence.Children[i]);
                FlattenNode(
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{item:{Hash(identity)}#{SequenceOccurrenceAt(sequence, i, identity)}}}",
                    sequence.Children[i],
                    values);
            }

            return;
        }

        values.Add(new(fieldPath, semanticPath, ScalarValue(node)));
    }

    private static Dictionary<string, YamlNode> ToMapping(YamlMappingNode mapping)
    {
        var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                throw new InvalidDataException("Manifest YAML contains a non-scalar mapping key.");
            }

            values.Add(key, valueNode);
        }

        return values;
    }

    private static string CanonicalNode(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
        {
            string value = scalar.Value ?? string.Empty;
            return $"s{value.Length}:{value}";
        }

        if (node is YamlSequenceNode sequence)
        {
            return $"q[{string.Join(',', sequence.Children.Select(CanonicalNode))}]";
        }

        if (node is YamlMappingNode mapping)
        {
            return $"m{{{string.Join(
                ',',
                ToMapping(mapping)
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => $"{pair.Key.Length}:{pair.Key}={CanonicalNode(pair.Value)}"))}}}";
        }

        return node.ToString();
    }

    private static string Hash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string AppendProperty(string path, string property)
        => path.Length == 0 ? property : $"{path}.{property}";

    private static string? ScalarValue(YamlNode? node)
        => node switch
        {
            null => null,
            YamlScalarNode scalar => scalar.Value,
            _ => node.ToString(),
        };

    private sealed record DocumentSnapshot(string ManifestPath, YamlNode Root);

    private sealed record SequencePair(int BeforeIndex, int AfterIndex);

    private sealed record InstallerMatchValues(
        string PrimaryIdentity,
        string? UrlPattern,
        string? InstallerType,
        string? Scope,
        string? Locale,
        string? NestedInstallerType,
        string? Architecture,
        string? ProductCode);

    private sealed record FlattenedValue(string FieldPath, string SemanticPath, string? Value);
}

internal sealed record RawManifestChange(
    string DocumentKey,
    string ManifestPath,
    string FieldPath,
    string SemanticPath,
    string? Before,
    string? After);
