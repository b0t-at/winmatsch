using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Rules;

internal sealed class ManifestSnapshot
{
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
            Add(documents, $"locale:{i}", GetLocalePath(manifests, locale, i), ManifestYamlWriter.Serialize(locale));
        }

        return new(documents);
    }

    public static PackageManifests Clone(PackageManifests manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        string installerYaml = SerializeInstaller(manifests.Installer, out int[] missingHashIndices);
        var clone = new PackageManifests
        {
            Installer = ManifestYamlReader.ReadInstaller(installerYaml),
            DefaultLocale = ManifestYamlReader.ReadDefaultLocale(ManifestYamlWriter.Serialize(manifests.DefaultLocale)),
            Locales =
            [
                .. manifests.Locales.Select(
                    static locale => ManifestYamlReader.ReadLocale(ManifestYamlWriter.Serialize(locale))),
            ],
            Version = ManifestYamlReader.ReadVersion(ManifestYamlWriter.Serialize(manifests.Version)),
        };

        foreach (int index in missingHashIndices)
        {
            clone.Installer.Installers![index].InstallerSha256 = null;
        }

        return clone;
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
            snapshot = null!;
            return false;
        }
    }

    public IReadOnlyList<RawManifestChange> Diff(ManifestSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<RawManifestChange>();
        var paths = new SortedSet<string>(_documents.Keys, StringComparer.Ordinal);
        paths.UnionWith(after._documents.Keys);

        foreach (string documentKey in paths)
        {
            _documents.TryGetValue(documentKey, out DocumentSnapshot? beforeDocument);
            after._documents.TryGetValue(documentKey, out DocumentSnapshot? afterDocument);
            DiffNode(
                afterDocument?.ManifestPath ?? beforeDocument!.ManifestPath,
                fieldPath: string.Empty,
                beforeDocument?.Root,
                afterDocument?.Root,
                changes);
        }

        return changes;
    }

    public IReadOnlyDictionary<ManifestFieldKey, ManifestFieldValue> Flatten()
    {
        var values = new Dictionary<ManifestFieldKey, ManifestFieldValue>();
        foreach ((string documentKey, DocumentSnapshot document) in _documents)
        {
            FlattenNode(documentKey, document.ManifestPath, string.Empty, document.Root, values);
        }

        return values;
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
        string manifestPath,
        string fieldPath,
        YamlNode? before,
        YamlNode? after,
        ICollection<RawManifestChange> changes)
    {
        if (before is YamlMappingNode beforeMapping && after is YamlMappingNode afterMapping)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            var beforeValues = ToMapping(beforeMapping);
            var afterValues = ToMapping(afterMapping);
            keys.UnionWith(beforeValues.Keys);
            keys.UnionWith(afterValues.Keys);
            foreach (string key in keys)
            {
                beforeValues.TryGetValue(key, out YamlNode? beforeChild);
                afterValues.TryGetValue(key, out YamlNode? afterChild);
                DiffNode(manifestPath, AppendProperty(fieldPath, key), beforeChild, afterChild, changes);
            }

            return;
        }

        if (before is YamlSequenceNode beforeSequence && after is YamlSequenceNode afterSequence)
        {
            int count = Math.Max(beforeSequence.Children.Count, afterSequence.Children.Count);
            for (int i = 0; i < count; i++)
            {
                DiffNode(
                    manifestPath,
                    $"{fieldPath}[{i}]",
                    i < beforeSequence.Children.Count ? beforeSequence.Children[i] : null,
                    i < afterSequence.Children.Count ? afterSequence.Children[i] : null,
                    changes);
            }

            return;
        }

        if (before is null && after is YamlMappingNode or YamlSequenceNode)
        {
            FlattenAddedOrRemoved(manifestPath, fieldPath, after, beforeValue: null, adding: true, changes);
            return;
        }

        if (after is null && before is YamlMappingNode or YamlSequenceNode)
        {
            FlattenAddedOrRemoved(manifestPath, fieldPath, before, beforeValue: null, adding: false, changes);
            return;
        }

        string? beforeValue = ScalarValue(before);
        string? afterValue = ScalarValue(after);
        if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal)
            || before?.NodeType != after?.NodeType)
        {
            changes.Add(new(manifestPath, fieldPath, beforeValue, afterValue));
        }
    }

    private static void FlattenAddedOrRemoved(
        string manifestPath,
        string fieldPath,
        YamlNode node,
        string? beforeValue,
        bool adding,
        ICollection<RawManifestChange> changes)
    {
        var values = new Dictionary<ManifestFieldKey, ManifestFieldValue>();
        FlattenNode("diff", manifestPath, fieldPath, node, values);
        foreach ((ManifestFieldKey path, ManifestFieldValue value) in values.OrderBy(static pair => pair.Key.FieldPath, StringComparer.Ordinal))
        {
            changes.Add(new(
                value.ManifestPath,
                path.FieldPath,
                adding ? beforeValue : value.Value,
                adding ? value.Value : beforeValue));
        }
    }

    private static void FlattenNode(
        string documentKey,
        string manifestPath,
        string fieldPath,
        YamlNode node,
        IDictionary<ManifestFieldKey, ManifestFieldValue> values)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach ((string key, YamlNode value) in ToMapping(mapping).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                FlattenNode(documentKey, manifestPath, AppendProperty(fieldPath, key), value, values);
            }

            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (int i = 0; i < sequence.Children.Count; i++)
            {
                FlattenNode(documentKey, manifestPath, $"{fieldPath}[{i}]", sequence.Children[i], values);
            }

            return;
        }

        values[new(documentKey, fieldPath)] = new(manifestPath, ScalarValue(node));
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
}

internal sealed record RawManifestChange(
    string ManifestPath,
    string FieldPath,
    string? Before,
    string? After);

internal readonly record struct ManifestFieldKey(string DocumentKey, string FieldPath);

internal sealed record ManifestFieldValue(string ManifestPath, string? Value);
