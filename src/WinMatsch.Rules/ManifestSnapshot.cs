using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Rules;

internal sealed class ManifestSnapshot
{
    private const int InstallerMatchThreshold = 150;
    private const int MaximumInstallerMatchComparisons = 1_000_000;
    private const int MaximumLocaleMatchComparisons = 1_000_000;
    private readonly IReadOnlyDictionary<string, DocumentSnapshot> _documents;

    private ManifestSnapshot(IReadOnlyDictionary<string, DocumentSnapshot> documents)
    {
        _documents = documents;
    }

    public static ManifestSnapshot Capture(PackageManifests manifests)
        => Capture(manifests, manifests);

    private static ManifestSnapshot Capture(
        PackageManifests serializableManifests,
        PackageManifests identitySource)
    {
        ArgumentNullException.ThrowIfNull(serializableManifests);
        ArgumentNullException.ThrowIfNull(identitySource);
        var documents = new SortedDictionary<string, DocumentSnapshot>(StringComparer.Ordinal);

        Add(
            documents,
            "version",
            GetVersionPath(identitySource),
            ManifestYamlWriter.Serialize(serializableManifests.Version));
        Add(
            documents,
            "installer",
            GetInstallerPath(identitySource),
            ManifestYamlWriter.Serialize(serializableManifests.Installer));
        Add(
            documents,
            "defaultLocale",
            GetDefaultLocalePath(identitySource),
            ManifestYamlWriter.Serialize(serializableManifests.DefaultLocale));

        string[] localeDocumentKeys = GetLocaleDocumentKeys(identitySource.Locales);
        for (int i = 0; i < serializableManifests.Locales.Count; i++)
        {
            LocaleManifest locale = serializableManifests.Locales[i];
            LocaleManifest identityLocale = identitySource.Locales[i];
            Add(
                documents,
                localeDocumentKeys[i],
                GetLocalePath(identitySource, identityLocale, i),
                ManifestYamlWriter.Serialize(locale));
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
                snapshot = Capture(ManifestClone.CreateSerializable(manifests), manifests);
                snapshot.RemoveMissingRequiredValues(manifests);
                return true;
            }
            catch (InvalidOperationException)
            {
                snapshot = null!;
                return false;
            }
        }
    }

    private void RemoveMissingRequiredValues(PackageManifests original)
        {
            YamlMappingNode version = GetRoot("version");
            RemoveWhenMissing(version, "PackageIdentifier", original.Version.PackageIdentifier);
            RemoveWhenMissing(version, "PackageVersion", original.Version.PackageVersion);
            RemoveWhenMissing(version, "DefaultLocale", original.Version.DefaultLocale);
            RemoveWhenMissing(version, "ManifestVersion", original.Version.ManifestVersion);

            YamlMappingNode installerManifest = GetRoot("installer");
            RemoveWhenMissing(installerManifest, "PackageIdentifier", original.Installer.PackageIdentifier);
            RemoveWhenMissing(installerManifest, "PackageVersion", original.Installer.PackageVersion);
            RemoveWhenMissing(installerManifest, "ManifestVersion", original.Installer.ManifestVersion);
            RemoveMissingInstallerFields(installerManifest, original.Installer);
            if (original.Installer.Installers is not { Count: > 0 } originalInstallers)
            {
                RemoveMappingKey(installerManifest, "Installers");
            }
            else if (GetMappingValue(installerManifest, "Installers") is YamlSequenceNode installerSequence)
            {
                for (int i = 0; i < originalInstallers.Count; i++)
                {
                    if (installerSequence.Children[i] is not YamlMappingNode installer)
                    {
                        continue;
                    }

                    Installer originalInstaller = originalInstallers[i];
                    RemoveWhenMissing(installer, "Architecture", originalInstaller.Architecture);
                    RemoveWhenMissing(installer, "InstallerUrl", originalInstaller.InstallerUrl);
                    RemoveWhenMissing(installer, "InstallerSha256", originalInstaller.InstallerSha256);
                    RemoveMissingInstallerFields(installer, originalInstaller);
                }
            }

            RemoveMissingLocaleValues(GetRoot("defaultLocale"), original.DefaultLocale);
            string[] localeDocumentKeys = GetLocaleDocumentKeys(original.Locales);
            for (int i = 0; i < original.Locales.Count; i++)
            {
                LocaleManifest locale = original.Locales[i];
                RemoveMissingLocaleValues(GetRoot(localeDocumentKeys[i]), locale);
            }
        }

        private YamlMappingNode GetRoot(string documentKey)
            => (YamlMappingNode)_documents[documentKey].Root;

        private static void RemoveMissingLocaleValues(YamlMappingNode mapping, LocaleManifest original)
        {
            RemoveWhenMissing(mapping, "PackageIdentifier", original.PackageIdentifier);
            RemoveWhenMissing(mapping, "PackageVersion", original.PackageVersion);
            RemoveWhenMissing(mapping, "PackageLocale", original.PackageLocale);
            RemoveWhenMissing(mapping, "ManifestVersion", original.ManifestVersion);
            if (original is DefaultLocaleManifest defaultLocale)
            {
                RemoveWhenMissing(mapping, "Publisher", defaultLocale.Publisher);
                RemoveWhenMissing(mapping, "PackageName", defaultLocale.PackageName);
                RemoveWhenMissing(mapping, "License", defaultLocale.License);
                RemoveWhenMissing(mapping, "ShortDescription", defaultLocale.ShortDescription);
            }

            if (original.Icons is { } icons
                && GetMappingValue(mapping, "Icons") is YamlSequenceNode iconSequence)
            {
                for (int i = 0; i < icons.Count; i++)
                {
                    if (iconSequence.Children[i] is YamlMappingNode icon)
                    {
                        RemoveWhenMissing(icon, "IconUrl", icons[i].IconUrl);
                        RemoveWhenMissing(icon, "IconFileType", icons[i].IconFileType);
                    }
                }
            }
        }

        private static void RemoveMissingInstallerFields(YamlMappingNode mapping, InstallerFieldsBase original)
        {
            RemoveMissingSequenceMappingValues(
                mapping,
                "ExpectedReturnCodes",
                original.ExpectedReturnCodes,
                static (item, value) =>
                {
                    RemoveWhenMissing(item, "InstallerReturnCode", value.InstallerReturnCode);
                    RemoveWhenMissing(item, "ReturnResponse", value.ReturnResponse);
                });
            RemoveMissingSequenceMappingValues(
                mapping,
                "NestedInstallerFiles",
                original.NestedInstallerFiles,
                static (item, value) => RemoveWhenMissing(item, "RelativeFilePath", value.RelativeFilePath));

            if (original.Dependencies?.PackageDependencies is { } dependencies
                && GetMappingValue(mapping, "Dependencies") is YamlMappingNode dependencyMapping)
            {
                RemoveMissingSequenceMappingValues(
                    dependencyMapping,
                    "PackageDependencies",
                    dependencies,
                    static (item, value) => RemoveWhenMissing(item, "PackageIdentifier", value.PackageIdentifier));
            }

            if (original.InstallationMetadata?.Files is { } files
                && GetMappingValue(mapping, "InstallationMetadata") is YamlMappingNode metadataMapping)
            {
                RemoveMissingSequenceMappingValues(
                    metadataMapping,
                    "Files",
                    files,
                    static (item, value) => RemoveWhenMissing(item, "RelativeFilePath", value.RelativeFilePath));
            }

            if (original.Authentication is { } authentication
                && GetMappingValue(mapping, "Authentication") is YamlMappingNode authenticationMapping)
            {
                RemoveWhenMissing(
                    authenticationMapping,
                    "AuthenticationType",
                    authentication.AuthenticationType);
            }
        }

        private static void RemoveMissingSequenceMappingValues<T>(
            YamlMappingNode parent,
            string key,
            IReadOnlyList<T>? originals,
            Action<YamlMappingNode, T> removeMissing)
        {
            if (originals is null || GetMappingValue(parent, key) is not YamlSequenceNode sequence)
            {
                return;
            }

            for (int i = 0; i < originals.Count; i++)
            {
                if (sequence.Children[i] is YamlMappingNode mapping)
                {
                    removeMissing(mapping, originals[i]);
                }
            }
        }

        private static void RemoveWhenMissing<T>(YamlMappingNode mapping, string key, T? value)
        {
            if (value is null)
            {
                RemoveMappingKey(mapping, key);
            }
        }

        private static YamlNode? GetMappingValue(YamlMappingNode mapping, string key)
        {
            foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
            {
                if (keyNode is YamlScalarNode { Value: { } value }
                    && string.Equals(value, key, StringComparison.Ordinal))
                {
                    return valueNode;
                }
            }

            return null;
        }

        private static void RemoveMappingKey(YamlMappingNode mapping, string key)
        {
            YamlNode? matchedKey = null;
            foreach (YamlNode keyNode in mapping.Children.Keys)
            {
                if (keyNode is YamlScalarNode { Value: { } value }
                    && string.Equals(value, key, StringComparison.Ordinal))
                {
                    matchedKey = keyNode;
                    break;
                }
            }

            if (matchedKey is not null)
            {
                mapping.Children.Remove(matchedKey);
            }
        }

    public IReadOnlyList<RawManifestChange> Diff(ManifestSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<RawManifestChange>();
        foreach (DocumentPair pair in MatchDocuments(after))
        {
            DiffNode(
                pair.SemanticKey,
                pair.After?.ManifestPath ?? pair.Before!.ManifestPath,
                fieldPath: string.Empty,
                semanticPath: string.Empty,
                pair.Before?.Root,
                pair.After?.Root,
                changes);
        }

        return changes;
    }

    private List<DocumentPair> MatchDocuments(ManifestSnapshot after)
    {
        var pairs = new List<DocumentPair>();
        var matchedBefore = new HashSet<string>(StringComparer.Ordinal);
        var matchedAfter = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in _documents.Keys.Where(static key => !key.StartsWith("locale:", StringComparison.Ordinal)))
        {
            _documents.TryGetValue(key, out DocumentSnapshot? beforeDocument);
            after._documents.TryGetValue(key, out DocumentSnapshot? afterDocument);
            pairs.Add(new(key, beforeDocument, afterDocument));
            matchedBefore.Add(key);
            if (afterDocument is not null)
            {
                matchedAfter.Add(key);
            }
        }

        foreach (string key in after._documents.Keys.Where(
                     static key => !key.StartsWith("locale:", StringComparison.Ordinal)
                         && key is not "version" and not "installer" and not "defaultLocale"))
        {
            if (!matchedAfter.Contains(key))
            {
                pairs.Add(new(key, null, after._documents[key]));
                matchedAfter.Add(key);
            }
        }

        string[] localeBases = _documents.Keys
            .Concat(after._documents.Keys)
            .Where(static key => key.StartsWith("locale:", StringComparison.Ordinal))
            .Select(LocaleBaseKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string localeBase in localeBases)
        {
            string[] beforeKeys = _documents.Keys
                .Where(key => string.Equals(LocaleBaseKey(key), localeBase, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] afterKeys = after._documents.Keys
                .Where(key => string.Equals(LocaleBaseKey(key), localeBase, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var usedBefore = new HashSet<int>();
            var usedAfter = new HashSet<int>();
            var afterByContent = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int afterIndex = 0; afterIndex < afterKeys.Length; afterIndex++)
            {
                string content = CanonicalNode(after._documents[afterKeys[afterIndex]].Root);
                if (!afterByContent.TryGetValue(content, out Queue<int>? indices))
                {
                    indices = new();
                    afterByContent.Add(content, indices);
                }

                indices.Enqueue(afterIndex);
            }

            for (int beforeIndex = 0; beforeIndex < beforeKeys.Length; beforeIndex++)
            {
                string content = CanonicalNode(_documents[beforeKeys[beforeIndex]].Root);
                if (afterByContent.TryGetValue(content, out Queue<int>? indices)
                    && indices.Count > 0)
                {
                    int afterIndex = indices.Dequeue();
                    usedBefore.Add(beforeIndex);
                    usedAfter.Add(afterIndex);
                    string beforeKey = beforeKeys[beforeIndex];
                    string afterKey = afterKeys[afterIndex];
                    pairs.Add(new(beforeKey, _documents[beforeKey], after._documents[afterKey]));
                }
            }

            int remainingBefore = beforeKeys.Length - usedBefore.Count;
            int remainingAfter = afterKeys.Length - usedAfter.Count;
            if ((long)remainingBefore * remainingAfter <= MaximumLocaleMatchComparisons)
            {
                var candidates = new List<DocumentMatchCandidate>();
                for (int beforeIndex = 0; beforeIndex < beforeKeys.Length; beforeIndex++)
                {
                    if (usedBefore.Contains(beforeIndex))
                    {
                        continue;
                    }

                    for (int afterIndex = 0; afterIndex < afterKeys.Length; afterIndex++)
                    {
                        if (!usedAfter.Contains(afterIndex))
                        {
                            candidates.Add(new(
                                beforeIndex,
                                afterIndex,
                                DocumentSimilarity(
                                    _documents[beforeKeys[beforeIndex]].Root,
                                    after._documents[afterKeys[afterIndex]].Root)));
                        }
                    }
                }

                foreach (DocumentMatchCandidate candidate in candidates
                             .OrderByDescending(static candidate => candidate.Score)
                             .ThenBy(static candidate => candidate.BeforeIndex)
                             .ThenBy(static candidate => candidate.AfterIndex))
                {
                    if (!usedBefore.Contains(candidate.BeforeIndex)
                        && !usedAfter.Contains(candidate.AfterIndex))
                    {
                        usedBefore.Add(candidate.BeforeIndex);
                        usedAfter.Add(candidate.AfterIndex);
                        string beforeKey = beforeKeys[candidate.BeforeIndex];
                        string afterKey = afterKeys[candidate.AfterIndex];
                        pairs.Add(new(beforeKey, _documents[beforeKey], after._documents[afterKey]));
                    }
                }
            }
            else
            {
                int[] unmatchedBefore = Enumerable.Range(0, beforeKeys.Length)
                    .Where(index => !usedBefore.Contains(index))
                    .ToArray();
                int[] unmatchedAfter = Enumerable.Range(0, afterKeys.Length)
                    .Where(index => !usedAfter.Contains(index))
                    .ToArray();
                int count = Math.Min(unmatchedBefore.Length, unmatchedAfter.Length);
                for (int i = 0; i < count; i++)
                {
                    int beforeIndex = unmatchedBefore[i];
                    int afterIndex = unmatchedAfter[i];
                    usedBefore.Add(beforeIndex);
                    usedAfter.Add(afterIndex);
                    pairs.Add(new(
                        beforeKeys[beforeIndex],
                        _documents[beforeKeys[beforeIndex]],
                        after._documents[afterKeys[afterIndex]]));
                }
            }

            for (int i = 0; i < beforeKeys.Length; i++)
            {
                if (!usedBefore.Contains(i))
                {
                    pairs.Add(new(beforeKeys[i], _documents[beforeKeys[i]], null));
                }
            }

            var addedOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < afterKeys.Length; i++)
            {
                if (!usedAfter.Contains(i))
                {
                    string hash = Hash(CanonicalNode(after._documents[afterKeys[i]].Root));
                    int occurrence = addedOccurrences.GetValueOrDefault(hash);
                    addedOccurrences[hash] = occurrence + 1;
                    string semanticKey = $"{localeBase}#added:{hash}#{occurrence}";
                    pairs.Add(new(semanticKey, null, after._documents[afterKeys[i]]));
                }
            }
        }

        return pairs;
    }

    private static string LocaleBaseKey(string key)
    {
        int occurrence = key.LastIndexOf('#');
        return occurrence < 0 ? key : key[..occurrence];
    }

    private static int DocumentSimilarity(YamlNode before, YamlNode after)
    {
        if (string.Equals(CanonicalNode(before), CanonicalNode(after), StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        Dictionary<string, string?> beforeValues = FlattenForSimilarity(before);
        Dictionary<string, string?> afterValues = FlattenForSimilarity(after);
        int score = 0;
        foreach ((string path, string? value) in beforeValues)
        {
            if (afterValues.TryGetValue(path, out string? afterValue)
                && string.Equals(value, afterValue, StringComparison.Ordinal))
            {
                score++;
            }
        }

        return score;
    }

    private static Dictionary<string, string?> FlattenForSimilarity(YamlNode root)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        FlattenForSimilarity(root, string.Empty, values);
        return values;
    }

    private static void FlattenForSimilarity(
        YamlNode node,
        string path,
        IDictionary<string, string?> values)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach ((string key, YamlNode child) in ToMapping(mapping))
            {
                FlattenForSimilarity(child, AppendProperty(path, key), values);
            }

            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (int i = 0; i < sequence.Children.Count; i++)
            {
                FlattenForSimilarity(sequence.Children[i], $"{path}[{i}]", values);
            }

            return;
        }

        values[path] = ScalarValue(node);
    }

    public static string GetInstallerPath(PackageManifests manifests)
        => manifests.Installer.PackageIdentifier is { } identifier
            ? ManifestPaths.GetInstallerFileName(identifier)
            : "installer.yaml";

    internal static bool SemanticValueEquals(string fieldPath, string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return left is not null
            && right is not null
            && fieldPath.EndsWith(".InstallerUrl", StringComparison.Ordinal)
            && string.Equals(
                NormalizeInstallerUrl(left),
                NormalizeInstallerUrl(right),
                StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryGetEffectiveInstallerValue(string semanticPath, out string? value)
    {
        const string prefix = "Installers{installer:";
        if (!semanticPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return TryGetUniformInstallerValue(semanticPath, out value);
        }

        int close = semanticPath.IndexOf('}', prefix.Length);
        if (close < 0
            || close + 2 > semanticPath.Length
            || semanticPath[close + 1] != '.')
        {
            value = null;
            return false;
        }

        string fieldPath = semanticPath[(close + 2)..];

        string identityToken = semanticPath[prefix.Length..close];
        YamlMappingNode root = GetRoot("installer");
        if (GetMappingValue(root, "Installers") is not YamlSequenceNode installers)
        {
            return TryResolveSemanticPath(root, fieldPath, out value);
        }

        int[] occurrences = GetInstallerOccurrenceOrdinals(installers, root);
        for (int i = 0; i < installers.Children.Count; i++)
        {
            string identity = InstallerIdentity(installers.Children[i], root);
            string candidate = $"{Hash(identity)}#{occurrences[i]}";
            if (!string.Equals(candidate, identityToken, StringComparison.Ordinal)
                || installers.Children[i] is not YamlMappingNode installer)
            {
                continue;
            }

            if (TryResolveSemanticPath(installer, fieldPath, out value))
            {
                return true;
            }

            return TryResolveSemanticPath(root, fieldPath, out value);
        }

        value = null;
        return false;
    }

    private bool TryGetUniformInstallerValue(string semanticPath, out string? value)
    {
        int separator = semanticPath.IndexOfAny(['.', '{']);
        string rootField = separator < 0 ? semanticPath : semanticPath[..separator];
        if (!InstallerFieldAccessors.All.Any(
                accessor => string.Equals(accessor.Name, rootField, StringComparison.Ordinal)))
        {
            value = null;
            return false;
        }

        YamlMappingNode root = GetRoot("installer");
        if (TryResolveSemanticPath(root, semanticPath, out value))
        {
            return true;
        }

        if (GetMappingValue(root, "Installers") is not YamlSequenceNode installers
            || installers.Children.Count == 0)
        {
            value = null;
            return false;
        }

        string? commonValue = null;
        bool first = true;
        foreach (YamlNode node in installers.Children)
        {
            if (node is not YamlMappingNode installer
                || !TryResolveSemanticPath(installer, semanticPath, out string? installerValue))
            {
                value = null;
                return false;
            }

            if (first)
            {
                commonValue = installerValue;
                first = false;
            }
            else if (!string.Equals(commonValue, installerValue, StringComparison.Ordinal))
            {
                value = null;
                return false;
            }
        }

        value = commonValue;
        return true;
    }

    private static bool TryResolveSemanticPath(YamlNode start, string path, out string? value)
    {
        YamlNode? current = start;
        int position = 0;
        while (position < path.Length)
        {
            if (path[position] == '.')
            {
                position++;
                continue;
            }

            if (path[position] == '{')
            {
                int close = path.IndexOf('}', position + 1);
                if (close < 0
                    || current is not YamlSequenceNode sequence
                    || !TryResolveSequenceItem(sequence, path[(position + 1)..close], out current))
                {
                    value = null;
                    return false;
                }

                position = close + 1;
                continue;
            }

            int end = position;
            while (end < path.Length && path[end] is not '.' and not '{')
            {
                end++;
            }

            if (current is not YamlMappingNode mapping
                || GetMappingValue(mapping, path[position..end]) is not { } child)
            {
                value = null;
                return false;
            }

            current = child;
            position = end;
        }

        value = ScalarValue(current);
        return true;
    }

    private static bool TryResolveSequenceItem(
        YamlSequenceNode sequence,
        string identityToken,
        out YamlNode? value)
    {
        const string prefix = "item:";
        if (!identityToken.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        string[] identities = [.. sequence.Children.Select(CanonicalNode)];
        int[] occurrences = GetOccurrenceOrdinals(identities);
        for (int i = 0; i < identities.Length; i++)
        {
            string candidate = $"{Hash(identities[i])}#{occurrences[i]}";
            if (string.Equals(candidate, identityToken[prefix.Length..], StringComparison.Ordinal))
            {
                value = sequence.Children[i];
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string[] GetLocaleDocumentKeys(IReadOnlyList<LocaleManifest> locales)
    {
        string[] baseKeys =
        [
            .. locales.Select(
                static (locale, index) => locale.PackageLocale is { } packageLocale
                    ? $"locale:{packageLocale.Value.ToUpperInvariant()}"
                    : $"locale:missing:{index}"),
        ];
        Dictionary<string, int> totals = baseKeys
            .GroupBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keys = new string[baseKeys.Length];
        for (int i = 0; i < baseKeys.Length; i++)
        {
            string baseKey = baseKeys[i];
            int occurrence = occurrences.GetValueOrDefault(baseKey);
            occurrences[baseKey] = occurrence + 1;
            keys[i] = totals[baseKey] == 1 ? baseKey : $"{baseKey}#{occurrence}";
        }

        return keys;
    }

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
                if (documentKey == "installer"
                    && fieldPath.Length == 0
                    && key == "Installers"
                    && beforeChild is YamlSequenceNode beforeInstallers
                    && afterChild is YamlSequenceNode afterInstallers)
                {
                    DiffInstallerSequence(
                        documentKey,
                        manifestPath,
                        "Installers",
                        "Installers",
                        beforeInstallers,
                        afterInstallers,
                        beforeMapping,
                        afterMapping,
                        changes);
                }
                else
                {
                    DiffNode(
                        documentKey,
                        manifestPath,
                        AppendProperty(fieldPath, key),
                        AppendProperty(semanticPath, key),
                        beforeChild,
                        afterChild,
                        changes);
                }
            }

            return;
        }

        if (before is YamlSequenceNode beforeSequence && after is YamlSequenceNode afterSequence)
        {
            DiffSequence(
                documentKey,
                manifestPath,
                fieldPath,
                semanticPath,
                beforeSequence,
                afterSequence,
                changes);

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
        YamlMappingNode beforeRoot,
        YamlMappingNode afterRoot,
        ICollection<RawManifestChange> changes)
    {
        List<SequencePair> pairs = MatchInstallerItems(before, after, beforeRoot, afterRoot);
        var matchedBefore = new HashSet<int>(pairs.Select(static pair => pair.BeforeIndex));
        var matchedAfter = new HashSet<int>(pairs.Select(static pair => pair.AfterIndex));
        int[] beforeOccurrences = GetInstallerOccurrenceOrdinals(before, beforeRoot);
        int[] afterOccurrences = GetInstallerOccurrenceOrdinals(after, afterRoot);

        foreach (SequencePair pair in pairs.OrderBy(static pair => pair.AfterIndex))
        {
            string identity = InstallerIdentity(before.Children[pair.BeforeIndex], beforeRoot);
            DiffNode(
                documentKey,
                manifestPath,
                $"{fieldPath}[{pair.AfterIndex}]",
                $"{semanticPath}{{installer:{Hash(identity)}#{beforeOccurrences[pair.BeforeIndex]}}}",
                before.Children[pair.BeforeIndex],
                after.Children[pair.AfterIndex],
                changes);
        }

        for (int i = 0; i < before.Children.Count; i++)
        {
            if (!matchedBefore.Contains(i))
            {
                string identity = InstallerIdentity(before.Children[i], beforeRoot);
                FlattenAddedOrRemoved(
                    documentKey,
                    manifestPath,
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{installer:{Hash(identity)}#{beforeOccurrences[i]}}}",
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
                string identity = InstallerIdentity(after.Children[i], afterRoot);
                FlattenAddedOrRemoved(
                    documentKey,
                    manifestPath,
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{installer:{Hash(identity)}#{afterOccurrences[i]}}}",
                    after.Children[i],
                    beforeValue: null,
                    adding: true,
                    changes);
            }
        }
    }

    private static List<SequencePair> MatchInstallerItems(
        YamlSequenceNode before,
        YamlSequenceNode after,
        YamlMappingNode beforeRoot,
        YamlMappingNode afterRoot)
    {
        InstallerMatchValues[] beforeValues =
        [
            .. before.Children.Select(node => GetInstallerMatchValues(node, beforeRoot)),
        ];
        InstallerMatchValues[] afterValues =
        [
            .. after.Children.Select(node => GetInstallerMatchValues(node, afterRoot)),
        ];
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

    private static InstallerMatchValues GetInstallerMatchValues(
        YamlNode node,
        YamlMappingNode? root = null)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new(CanonicalNode(node), null, null, null, null, null, null, null);
        }

        Dictionary<string, YamlNode> values = ToMapping(mapping);
        Dictionary<string, YamlNode>? rootValues = root is null ? null : ToMapping(root);
        string? urlPattern = GetScalar(values, "InstallerUrl") is { } url
            ? NormalizeInstallerUrl(url)
            : null;
        string? installerType = GetEffectiveScalar(values, rootValues, "InstallerType");
        string? scope = GetEffectiveScalar(values, rootValues, "Scope");
        string? locale = GetEffectiveScalar(values, rootValues, "InstallerLocale");
        string? nestedInstallerType = GetEffectiveScalar(values, rootValues, "NestedInstallerType");
        string? architecture = GetScalar(values, "Architecture");
        string? productCode = GetEffectiveScalar(values, rootValues, "ProductCode");
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

    private static string InstallerIdentity(YamlNode node, YamlMappingNode? root = null)
    {
        InstallerMatchValues values = GetInstallerMatchValues(node, root);
        return string.IsNullOrEmpty(values.PrimaryIdentity)
            ? CanonicalNode(node)
            : values.PrimaryIdentity;
    }

    private static string? GetScalar(Dictionary<string, YamlNode> values, string key)
        => values.TryGetValue(key, out YamlNode? node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string? GetEffectiveScalar(
        Dictionary<string, YamlNode> values,
        Dictionary<string, YamlNode>? rootValues,
        string key)
        => GetScalar(values, key)
            ?? (rootValues is null ? null : GetScalar(rootValues, key));

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
            ReadOnlySpan<char> prefix = GetAsciiLetterPrefix(path, start);
            bool architectureToken = digits.SequenceEqual("64")
                    && (prefix.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                        || prefix.EndsWith("win", StringComparison.OrdinalIgnoreCase)
                        || prefix.EndsWith("arm", StringComparison.OrdinalIgnoreCase)
                        || prefix.EndsWith("amd", StringComparison.OrdinalIgnoreCase))
                || digits.SequenceEqual("86")
                    && prefix.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                || digits.SequenceEqual("32")
                    && (prefix.EndsWith("win", StringComparison.OrdinalIgnoreCase)
                        || prefix.EndsWith("arm", StringComparison.OrdinalIgnoreCase)
                        || prefix.EndsWith("aarch", StringComparison.OrdinalIgnoreCase)
                        || path.AsSpan(i).StartsWith("bit", StringComparison.OrdinalIgnoreCase))
                || digits.SequenceEqual("64")
                    && (prefix.EndsWith("aarch", StringComparison.OrdinalIgnoreCase)
                        || path.AsSpan(i).StartsWith("bit", StringComparison.OrdinalIgnoreCase))
                || digits.SequenceEqual("386")
                    && prefix.EndsWith("i", StringComparison.OrdinalIgnoreCase)
                || (digits.SequenceEqual("7") || digits.SequenceEqual("8"))
                    && prefix.EndsWith("armv", StringComparison.OrdinalIgnoreCase);
            if (architectureToken)
            {
                normalized.Append(digits);
                continue;
            }

            normalized.Append('#');
            while (i < path.Length && path[i] == '.')
            {
                int dot = i;
                i++;
                int componentStart = i;
                while (i < path.Length && char.IsAsciiDigit(path[i]))
                {
                    i++;
                }

                if (componentStart == i)
                {
                    i = dot;
                    break;
                }
            }
        }

        return normalized.ToString();
    }

    private static ReadOnlySpan<char> GetAsciiLetterPrefix(string value, int end)
    {
        int start = end;
        while (start > 0 && char.IsAsciiLetter(value[start - 1]))
        {
            start--;
        }

        return value.AsSpan(start, end - start);
    }

    private static int[] GetInstallerOccurrenceOrdinals(
        YamlSequenceNode sequence,
        YamlMappingNode root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinals = new int[sequence.Children.Count];
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            string identity = InstallerIdentity(sequence.Children[i], root);
            ordinals[i] = counts.GetValueOrDefault(identity);
            counts[identity] = ordinals[i] + 1;
        }

        return ordinals;
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
        int[] beforeOccurrences = GetOccurrenceOrdinals(beforeValues);
        int[] afterOccurrences = GetOccurrenceOrdinals(afterValues);
        var afterIndices = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int index = 0; index < after.Children.Count; index++)
        {
            if (!afterIndices.TryGetValue(afterValues[index], out Queue<int>? indices))
            {
                indices = new();
                afterIndices.Add(afterValues[index], indices);
            }

            indices.Enqueue(index);
        }

        var matchedBefore = new HashSet<int>();
        var matchedAfter = new HashSet<int>();
        for (int beforeIndex = 0; beforeIndex < beforeValues.Length; beforeIndex++)
        {
            if (!afterIndices.TryGetValue(beforeValues[beforeIndex], out Queue<int>? indices)
                || indices.Count == 0)
            {
                continue;
            }

            matchedBefore.Add(beforeIndex);
            matchedAfter.Add(indices.Dequeue());
        }

        int[] unmatchedBefore = Enumerable.Range(0, before.Children.Count)
            .Where(index => !matchedBefore.Contains(index))
            .ToArray();
        int[] unmatchedAfter = Enumerable.Range(0, after.Children.Count)
            .Where(index => !matchedAfter.Contains(index))
            .ToArray();
        int paired = Math.Min(unmatchedBefore.Length, unmatchedAfter.Length);
        for (int i = 0; i < paired; i++)
        {
            int beforeIndex = unmatchedBefore[i];
            int afterIndex = unmatchedAfter[i];
            string identity = beforeValues[beforeIndex];
            DiffNode(
                documentKey,
                manifestPath,
                $"{fieldPath}[{afterIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{beforeOccurrences[beforeIndex]}}}",
                before.Children[beforeIndex],
                after.Children[afterIndex],
                changes);
        }

        for (int i = paired; i < unmatchedBefore.Length; i++)
        {
            int beforeIndex = unmatchedBefore[i];
            string identity = beforeValues[beforeIndex];
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                $"{fieldPath}[{beforeIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{beforeOccurrences[beforeIndex]}}}",
                before.Children[beforeIndex],
                beforeValue: null,
                adding: false,
                changes);
        }

        for (int i = paired; i < unmatchedAfter.Length; i++)
        {
            int afterIndex = unmatchedAfter[i];
            string identity = afterValues[afterIndex];
            FlattenAddedOrRemoved(
                documentKey,
                manifestPath,
                $"{fieldPath}[{afterIndex}]",
                $"{semanticPath}{{item:{Hash(identity)}#{afterOccurrences[afterIndex]}}}",
                after.Children[afterIndex],
                beforeValue: null,
                adding: true,
                changes);
        }
    }

    private static int[] GetOccurrenceOrdinals(string[] values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinals = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            ordinals[i] = counts.GetValueOrDefault(values[i]);
            counts[values[i]] = ordinals[i] + 1;
        }

        return ordinals;
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
            string[] identities = [.. sequence.Children.Select(CanonicalNode)];
            int[] occurrences = GetOccurrenceOrdinals(identities);
            for (int i = 0; i < sequence.Children.Count; i++)
            {
                string identity = identities[i];
                FlattenNode(
                    $"{fieldPath}[{i}]",
                    $"{semanticPath}{{item:{Hash(identity)}#{occurrences[i]}}}",
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

    private sealed record DocumentPair(
        string SemanticKey,
        DocumentSnapshot? Before,
        DocumentSnapshot? After);

    private sealed record DocumentMatchCandidate(
        int BeforeIndex,
        int AfterIndex,
        int Score);

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
