using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using WinMatsch.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Rules.OverridePacks;

/// <summary>
/// Explicit representation-model YAML reader/writer for override packs. It uses no reflection
/// and rejects unknown keys, aliases, excessive depth, excessive node counts, and oversized input.
/// </summary>
public static class OverridePackYaml
{
    private static readonly ConcurrentDictionary<string, object> _writeLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public const int MaximumDocumentLength = 1_048_576;
    public const int MaximumDepth = 32;
    public const int MaximumNodeCount = 10_000;
    public const int MaximumScalarLength = 65_536;

    public static OverridePack ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[MaximumDocumentLength + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = reader.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumDocumentLength || reader.Peek() >= 0)
        {
            throw new FormatException($"Override pack exceeds the {MaximumDocumentLength} character limit.");
        }

        return Read(new string(buffer, 0, length));
    }

    public static OverridePack Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (yaml.Length > MaximumDocumentLength)
        {
            throw new FormatException($"Override pack exceeds the {MaximumDocumentLength} character limit.");
        }

        var stream = new YamlStream();
        try
        {
            ValidateParserEvents(yaml);
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw new FormatException("Override pack is not valid YAML.", exception);
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("Override pack must contain exactly one YAML mapping document.");
        }

        int nodeCount = 0;
        ValidateTree(root, depth: 0, ref nodeCount);
        Mapping values = Mapping.Create(root, "override pack");
        values.RequireKnownKeys(
            "formatVersion",
            "packageIdentifier",
            "rules",
            "forcedArchitectures",
            "assetMappings",
            "scopeLayout",
            "versionSource",
            "metadataUrlReplacements",
            "preservedFields",
            "droppedFields",
            "learnedFields",
            "vanityUrls",
            "manualOnly",
            "policies",
            "quirks");

        int formatVersion = ParseInt32(values.RequiredScalar("formatVersion"), "formatVersion");
        if (formatVersion != OverridePack.CurrentFormatVersion)
        {
            throw new FormatException(
                $"Unsupported override-pack formatVersion '{formatVersion}'; expected {OverridePack.CurrentFormatVersion}.");
        }

        var pack = new OverridePack
        {
            FormatVersion = formatVersion,
            PackageIdentifier = new(values.RequiredScalar("packageIdentifier")),
            RuleModes = ParseRuleModes(values.OptionalMapping("rules")),
            ForcedArchitectures = ParseForcedArchitectures(values.OptionalSequence("forcedArchitectures")),
            AssetMappings = ParseAssetMappings(values.OptionalSequence("assetMappings")),
            ScopeLayout = ParseOptionalEnum<ScopeLayoutOverride>(values.OptionalScalar("scopeLayout"), "scopeLayout"),
            VersionSource = values.OptionalScalar("versionSource"),
            MetadataUrlReplacements = ParseStringMap(values.OptionalMapping("metadataUrlReplacements")),
            PreservedFields = ParseStringList(values.OptionalSequence("preservedFields"), "preservedFields"),
            DroppedFields = ParseStringList(values.OptionalSequence("droppedFields"), "droppedFields"),
            LearnedFields = ParseLearnedFields(values.OptionalSequence("learnedFields")),
            VanityUrls = ParseStringList(values.OptionalSequence("vanityUrls"), "vanityUrls"),
            ManualOnly = ParseOptionalBoolean(values.OptionalScalar("manualOnly"), "manualOnly"),
            Policies = ParsePolicies(values.OptionalSequence("policies")),
            Quirks = ParseQuirks(values.OptionalMapping("quirks")),
        };
        ValidateSemantics(pack);
        return pack;
    }

    public static void WriteFile(string path, OverridePack pack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pack);
        string fullPath = Path.GetFullPath(path);
        lock (_writeLocks.GetOrAdd(fullPath, static _ => new object()))
        {
            string yaml = Write(pack);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The override-pack path has no parent directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (FileStream stream = new(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                           bufferSize: 4096,
                           leaveOpen: true))
                {
                    writer.Write(yaml);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(
                        temporaryPath,
                        fullPath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    DurableFileSystem.ReplaceFile(temporaryPath, fullPath);
                }

                using FileStream committed = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                committed.Flush(flushToDisk: true);
                DurableFileSystem.FlushDirectory(directory);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    public static string Write(OverridePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.FormatVersion != OverridePack.CurrentFormatVersion)
        {
            throw new ArgumentException(
                $"Only override-pack formatVersion {OverridePack.CurrentFormatVersion} can be written.",
                nameof(pack));
        }

        int estimatedLength = ValidateForWrite(pack);
        var yaml = new StringBuilder(estimatedLength);
        yaml.AppendLine($"formatVersion: {pack.FormatVersion.ToString(CultureInfo.InvariantCulture)}");
        yaml.AppendLine($"packageIdentifier: {Quote(pack.PackageIdentifier.Value)}");
        WriteRuleModes(yaml, pack.RuleModes);
        WriteForcedArchitectures(yaml, pack.ForcedArchitectures);
        WriteAssetMappings(yaml, pack.AssetMappings);
        Scalar(yaml, "scopeLayout", pack.ScopeLayout?.ToString());
        Scalar(yaml, "versionSource", pack.VersionSource);
        WriteStringMap(yaml, "metadataUrlReplacements", pack.MetadataUrlReplacements);
        WriteStringList(yaml, "preservedFields", pack.PreservedFields);
        WriteStringList(yaml, "droppedFields", pack.DroppedFields);
        WriteLearnedFields(yaml, pack.LearnedFields);
        WriteStringList(yaml, "vanityUrls", pack.VanityUrls);
        yaml.AppendLine($"manualOnly: {(pack.ManualOnly ? "true" : "false")}");
        WritePolicies(yaml, pack.Policies);
        WriteQuirks(yaml, pack.Quirks);
        string result = yaml.ToString().ReplaceLineEndings("\n");
        if (result.Length > MaximumDocumentLength)
        {
            throw new ArgumentException(
                $"Override pack exceeds the {MaximumDocumentLength} character output limit.",
                nameof(pack));
        }

        _ = Read(result);
        return result;
    }

    private static int ValidateForWrite(OverridePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack.PackageIdentifier);
        ArgumentNullException.ThrowIfNull(pack.RuleModes);
        ArgumentNullException.ThrowIfNull(pack.MetadataUrlReplacements);
        ArgumentNullException.ThrowIfNull(pack.Quirks);
        int nodeCount = 7;
        long scalarOutputLength = ValidateScalar(pack.PackageIdentifier.Value, "packageIdentifier");

        if (pack.RuleModes.Count > 0)
        {
            nodeCount += 2 + (2 * pack.RuleModes.Count);
            foreach ((string ruleId, RuleMode mode) in pack.RuleModes)
            {
                scalarOutputLength += ValidateScalar(ruleId, "a rules key");
                ValidateEnum(mode, $"rules.{ruleId}");
            }
        }

        if (!pack.ForcedArchitectures.IsDefaultOrEmpty)
        {
            nodeCount += 2 + (9 * pack.ForcedArchitectures.Length);
            foreach (ForcedArchitectureOverride value in pack.ForcedArchitectures)
            {
                ArgumentNullException.ThrowIfNull(value);
                scalarOutputLength += ValidateScalar(value.AssetPattern, "forcedArchitectures.assetPattern");
                scalarOutputLength += ValidateScalar(value.SourceEvidence, "forcedArchitectures.sourceEvidence");
                ValidateEnum(value.Architecture, "forcedArchitectures.architecture");
                ValidateEnum(value.Confidence, "forcedArchitectures.confidence");
            }
        }

        if (!pack.AssetMappings.IsDefaultOrEmpty)
        {
            nodeCount += 2;
            foreach (AssetMappingOverride value in pack.AssetMappings)
            {
                ArgumentNullException.ThrowIfNull(value);
                nodeCount += 5;
                scalarOutputLength += ValidateScalar(value.AssetPattern, "assetMappings.assetPattern");
                scalarOutputLength += ValidateScalar(value.Entry, "assetMappings.entry");
                if (value.Architecture is { } architecture)
                {
                    nodeCount += 2;
                    ValidateEnum(architecture, "assetMappings.architecture");
                }

                if (value.InstallerType is { } installerType)
                {
                    nodeCount += 2;
                    ValidateEnum(installerType, "assetMappings.installerType");
                }

                if (value.Scope is { } scope)
                {
                    nodeCount += 2;
                    ValidateEnum(scope, "assetMappings.scope");
                }
            }
        }

        if (pack.ScopeLayout is { } scopeLayout)
        {
            nodeCount += 2;
            ValidateEnum(scopeLayout, "scopeLayout");
        }

        if (pack.VersionSource is { } versionSource)
        {
            nodeCount += 2;
            scalarOutputLength += ValidateScalar(versionSource, "versionSource");
        }

        if (pack.MetadataUrlReplacements.Count > 0)
        {
            nodeCount += 2 + (2 * pack.MetadataUrlReplacements.Count);
            foreach ((string source, string replacement) in pack.MetadataUrlReplacements)
            {
                scalarOutputLength += ValidateScalar(source, "a metadataUrlReplacements key");
                scalarOutputLength += ValidateScalar(replacement, $"metadataUrlReplacements.{source}");
            }

        }

        AddStringSequenceBudget(pack.PreservedFields, "preservedFields", ref nodeCount, ref scalarOutputLength);
        AddStringSequenceBudget(pack.DroppedFields, "droppedFields", ref nodeCount, ref scalarOutputLength);
        if (!pack.LearnedFields.IsDefaultOrEmpty)
        {
            nodeCount += 2 + (17 * pack.LearnedFields.Length);
            foreach (LearnedFieldOverride value in pack.LearnedFields)
            {
                ArgumentNullException.ThrowIfNull(value);
                scalarOutputLength += ValidateScalar(value.DocumentKey, "learnedFields.documentKey");
                scalarOutputLength += ValidateScalar(value.SemanticPath, "learnedFields.semanticPath");
                scalarOutputLength += ValidateScalar(value.Value, "learnedFields.value");
                scalarOutputLength += ValidateScalar(value.ValueSha256, "learnedFields.valueSha256");
                scalarOutputLength += ValidateScalar(value.BotValueSha256, "learnedFields.botValueSha256");
                scalarOutputLength += ValidateScalar(value.SourceFingerprint, "learnedFields.sourceFingerprint");
                scalarOutputLength += ValidateScalar(value.Source, "learnedFields.source");
                if (value.InstallerSelectorSha256 is { } selector)
                {
                    scalarOutputLength += ValidateScalar(selector, "learnedFields.installerSelectorSha256");
                }
                OverridePackFieldSelector.ValidateLearned(value);
            }
        }
        AddStringSequenceBudget(pack.VanityUrls, "vanityUrls", ref nodeCount, ref scalarOutputLength);

        if (!pack.Policies.IsDefaultOrEmpty)
        {
            nodeCount += 2 + (5 * pack.Policies.Length);
            foreach (PolicyAnnotation value in pack.Policies)
            {
                ArgumentNullException.ThrowIfNull(value);
                scalarOutputLength += ValidateScalar(value.Id, "policies.id");
                scalarOutputLength += ValidateScalar(value.Annotation, "policies.annotation");
            }
        }

        if (pack.Quirks.DisplayVersionFromEvidenceProperty is { } property)
        {
            nodeCount += 4;
            scalarOutputLength += ValidateScalar(property, "quirks.displayVersionFromEvidenceProperty");
        }

        if (nodeCount > MaximumNodeCount)
        {
            throw new ArgumentException(
                $"Override pack exceeds the maximum YAML node count of {MaximumNodeCount}.",
                nameof(pack));
        }

        const int maximumWriterDepth = 3;
        if (maximumWriterDepth > MaximumDepth)
        {
            throw new InvalidOperationException("The override-pack writer shape exceeds the configured YAML depth limit.");
        }

        long estimatedLength = 512L + (nodeCount * 64L) + scalarOutputLength;
        if (estimatedLength > MaximumDocumentLength)
        {
            throw new ArgumentException(
                $"Override pack exceeds the {MaximumDocumentLength} character output limit.",
                nameof(pack));
        }

        ValidateSemantics(pack);
        return (int)estimatedLength;
    }

    private static void AddStringSequenceBudget(
        ImmutableArray<string> values,
        string description,
        ref int nodeCount,
        ref long scalarOutputLength)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        nodeCount += 2 + values.Length;
        foreach (string value in values)
        {
            scalarOutputLength += ValidateScalar(value, $"an entry of {description}");
        }
    }

    private static int ValidateScalar(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{description}' must be a non-empty scalar value.");
        }

        if (value.Length > MaximumScalarLength)
        {
            throw new ArgumentException(
                $"'{description}' exceeds the {MaximumScalarLength} character scalar limit.");
        }

        int escapedLength = 2;
        foreach (char character in value)
        {
            escapedLength += character switch
            {
                '"' or '\\' or '\n' or '\r' or '\t' => 2,
                < ' ' or '\u0085' or '\u2028' or '\u2029' => 6,
                _ => 1,
            };
        }

        return escapedLength;
    }

    private static void ValidateSemantics(OverridePack pack)
    {
        if (pack.VersionSource is { } versionSource)
        {
            string normalized = versionSource.Trim();
            bool literal = normalized.StartsWith("literal:", StringComparison.OrdinalIgnoreCase)
                && PackageVersion.TryCreate(normalized["literal:".Length..].Trim(), out _);
            bool known = normalized.ToLowerInvariant() is
                "installer" or "installer.productversion" or "product-version"
                or "installer.fileversion" or "file-version"
                or "release" or "release.tag" or "release-tag" or "tag"
                or "url" or "url.token" or "url-token";
            if (!literal && !known)
            {
                throw new FormatException(
                    $"versionSource '{versionSource}' is unsupported; use installer.productVersion, installer.fileVersion, release-tag, url-token, or literal:<version>.");
            }
        }

        foreach ((string source, string replacement) in pack.MetadataUrlReplacements)
        {
            OverridePackFieldSelector.ValidateMetadataUrlReplacement(source, replacement);
        }

        foreach (string selector in pack.PreservedFields)
        {
            OverridePackFieldSelector.ValidateSelector(selector, "preservedFields");
        }

        foreach (string selector in pack.DroppedFields)
        {
            OverridePackFieldSelector.ValidateSelector(selector, "droppedFields");
        }

        foreach (LearnedFieldOverride learned in pack.LearnedFields)
        {
            OverridePackFieldSelector.ValidateLearned(learned);
        }
    }

    private static void ValidateEnum<T>(T value, string description)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"'{description}' has unsupported value '{value}'.");
        }
    }

    private static void ValidateParserEvents(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        int depth = 0;
        int nodeCount = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is MappingStart or SequenceStart)
            {
                depth++;
                if (depth > MaximumDepth)
                {
                    throw new FormatException($"Override pack exceeds the maximum YAML depth of {MaximumDepth}.");
                }
            }
            else if (parser.Current is MappingEnd or SequenceEnd)
            {
                depth--;
            }

            if (parser.Current is NodeEvent)
            {
                nodeCount++;
                if (nodeCount > MaximumNodeCount)
                {
                    throw new FormatException($"Override pack exceeds the maximum YAML node count of {MaximumNodeCount}.");
                }
            }

            if (parser.Current is AnchorAlias
                || parser.Current is NodeEvent { Anchor.IsEmpty: false }
                || parser.Current is NodeEvent { Tag.IsEmpty: false })
            {
                throw new FormatException("YAML anchors, aliases, and explicit tags are not allowed in override packs.");
            }
        }
    }

    private static ImmutableDictionary<string, RuleMode> ParseRuleModes(YamlMappingNode? node)
    {
        var values = ImmutableDictionary.CreateBuilder<string, RuleMode>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
        {
            return values.ToImmutable();
        }

        foreach ((string id, YamlNode value) in Mapping.Create(node, "rules").Values)
        {
            values.Add(id, ParseEnum<RuleMode>(ScalarValue(value, $"rules.{id}"), $"rules.{id}"));
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<ForcedArchitectureOverride> ParseForcedArchitectures(YamlSequenceNode? sequence)
    {
        if (sequence is null)
        {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<ForcedArchitectureOverride>();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            Mapping item = Mapping.Create(RequireMapping(sequence.Children[i], $"forcedArchitectures[{i}]"), $"forcedArchitectures[{i}]");
            item.RequireKnownKeys("assetPattern", "architecture", "sourceEvidence", "confidence");
            values.Add(new()
            {
                AssetPattern = item.RequiredScalar("assetPattern"),
                Architecture = ParseEnum<Architecture>(item.RequiredScalar("architecture"), $"forcedArchitectures[{i}].architecture"),
                SourceEvidence = item.RequiredScalar("sourceEvidence"),
                Confidence = ParseOptionalEnum<RuleChangeConfidence>(item.OptionalScalar("confidence"), $"forcedArchitectures[{i}].confidence")
                    ?? RuleChangeConfidence.High,
            });
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<AssetMappingOverride> ParseAssetMappings(YamlSequenceNode? sequence)
    {
        if (sequence is null)
        {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<AssetMappingOverride>();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            Mapping item = Mapping.Create(RequireMapping(sequence.Children[i], $"assetMappings[{i}]"), $"assetMappings[{i}]");
            item.RequireKnownKeys("assetPattern", "entry", "architecture", "installerType", "scope");
            values.Add(new()
            {
                AssetPattern = item.RequiredScalar("assetPattern"),
                Entry = item.RequiredScalar("entry"),
                Architecture = ParseOptionalEnum<Architecture>(item.OptionalScalar("architecture"), $"assetMappings[{i}].architecture"),
                InstallerType = ParseOptionalEnum<InstallerType>(item.OptionalScalar("installerType"), $"assetMappings[{i}].installerType"),
                Scope = ParseOptionalEnum<Scope>(item.OptionalScalar("scope"), $"assetMappings[{i}].scope"),
            });
        }

        return values.ToImmutable();
    }

    private static ImmutableDictionary<string, string> ParseStringMap(YamlMappingNode? node)
    {
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (node is not null)
        {
            foreach ((string key, YamlNode value) in Mapping.Create(node, "metadataUrlReplacements").Values)
            {
                values.Add(key, ScalarValue(value, $"metadataUrlReplacements.{key}"));
            }
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<string> ParseStringList(YamlSequenceNode? sequence, string description)
    {
        if (sequence is null)
        {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<string>();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            values.Add(ScalarValue(sequence.Children[i], $"{description}[{i}]"));
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<PolicyAnnotation> ParsePolicies(YamlSequenceNode? sequence)
    {
        if (sequence is null)
        {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<PolicyAnnotation>();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            Mapping item = Mapping.Create(RequireMapping(sequence.Children[i], $"policies[{i}]"), $"policies[{i}]");
            item.RequireKnownKeys("id", "annotation");
            values.Add(new() { Id = item.RequiredScalar("id"), Annotation = item.RequiredScalar("annotation") });
        }

        return values.ToImmutable();
    }

    private static ImmutableArray<LearnedFieldOverride> ParseLearnedFields(YamlSequenceNode? sequence)
    {
        if (sequence is null)
        {
            return [];
        }

        var values = ImmutableArray.CreateBuilder<LearnedFieldOverride>();
        for (int i = 0; i < sequence.Children.Count; i++)
        {
            Mapping item = Mapping.Create(
                RequireMapping(sequence.Children[i], $"learnedFields[{i}]"),
                $"learnedFields[{i}]");
            item.RequireKnownKeys(
                "documentKey",
                "semanticPath",
                "value",
                "valueSha256",
                "botValueSha256",
                "sourceFingerprint",
                "source",
                "installerSelectorSha256");
            var learned = new LearnedFieldOverride
            {
                DocumentKey = item.RequiredScalar("documentKey"),
                SemanticPath = item.RequiredScalar("semanticPath"),
                Value = item.RequiredScalar("value"),
                ValueSha256 = item.RequiredScalar("valueSha256"),
                BotValueSha256 = item.RequiredScalar("botValueSha256"),
                SourceFingerprint = item.RequiredScalar("sourceFingerprint"),
                Source = item.RequiredScalar("source"),
                InstallerSelectorSha256 = item.OptionalScalar("installerSelectorSha256"),
            };
            OverridePackFieldSelector.ValidateLearned(learned);
            values.Add(learned);
        }

        return values.ToImmutable();
    }

    private static PackageQuirks ParseQuirks(YamlMappingNode? node)
    {
        if (node is null)
        {
            return new();
        }

        Mapping values = Mapping.Create(node, "quirks");
        values.RequireKnownKeys("displayVersionFromEvidenceProperty");
        return new() { DisplayVersionFromEvidenceProperty = values.OptionalScalar("displayVersionFromEvidenceProperty") };
    }

    private static void ValidateTree(YamlNode node, int depth, ref int nodeCount)
    {
        if (depth > MaximumDepth)
        {
            throw new FormatException($"Override pack exceeds the maximum YAML depth of {MaximumDepth}.");
        }

        nodeCount++;
        if (nodeCount > MaximumNodeCount)
        {
            throw new FormatException($"Override pack exceeds the maximum YAML node count of {MaximumNodeCount}.");
        }

        if (node.NodeType == YamlNodeType.Alias)
        {
            throw new FormatException("YAML aliases are not allowed in override packs.");
        }

        if (node is YamlScalarNode { Value: { } value } && value.Length > MaximumScalarLength)
        {
            throw new FormatException($"Override pack scalar exceeds the {MaximumScalarLength} character limit.");
        }

        if (node is YamlMappingNode mapping)
        {
            foreach ((YamlNode key, YamlNode child) in mapping.Children)
            {
                ValidateTree(key, depth + 1, ref nodeCount);
                ValidateTree(child, depth + 1, ref nodeCount);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode child in sequence.Children)
            {
                ValidateTree(child, depth + 1, ref nodeCount);
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
        => node as YamlMappingNode ?? throw new FormatException($"'{description}' must be a mapping.");

    private static string ScalarValue(YamlNode node, string description)
    {
        if (node is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"'{description}' must be a non-empty scalar.");
        }

        return value;
    }

    private static int ParseInt32(string value, string description)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new FormatException($"'{description}' must be an integer.");

    private static bool ParseOptionalBoolean(string? value, string description)
        => value is null
            ? false
            : bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new FormatException($"'{description}' must be true or false.");

    private static T ParseEnum<T>(string value, string description)
        where T : struct, Enum
        => Enum.TryParse(value, ignoreCase: true, out T parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FormatException($"'{description}' has unsupported value '{value}'.");

    private static T? ParseOptionalEnum<T>(string? value, string description)
        where T : struct, Enum
        => value is null ? null : ParseEnum<T>(value, description);

    private static void WriteRuleModes(StringBuilder yaml, ImmutableDictionary<string, RuleMode> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        yaml.AppendLine("rules:");
        foreach ((string id, RuleMode mode) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            yaml.AppendLine($"  {Quote(id)}: {mode}");
        }
    }

    private static void WriteForcedArchitectures(StringBuilder yaml, ImmutableArray<ForcedArchitectureOverride> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        yaml.AppendLine("forcedArchitectures:");
        foreach (ForcedArchitectureOverride value in values)
        {
            yaml.AppendLine($"  - assetPattern: {Quote(value.AssetPattern)}");
            yaml.AppendLine($"    architecture: {value.Architecture}");
            yaml.AppendLine($"    sourceEvidence: {Quote(value.SourceEvidence)}");
            yaml.AppendLine($"    confidence: {value.Confidence}");
        }
    }

    private static void WriteAssetMappings(StringBuilder yaml, ImmutableArray<AssetMappingOverride> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        yaml.AppendLine("assetMappings:");
        foreach (AssetMappingOverride value in values)
        {
            yaml.AppendLine($"  - assetPattern: {Quote(value.AssetPattern)}");
            yaml.AppendLine($"    entry: {Quote(value.Entry)}");
            Scalar(yaml, "architecture", value.Architecture?.ToString(), indentation: 4);
            Scalar(yaml, "installerType", value.InstallerType?.ToString(), indentation: 4);
            Scalar(yaml, "scope", value.Scope?.ToString(), indentation: 4);
        }
    }

    private static void WriteStringMap(StringBuilder yaml, string key, ImmutableDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        yaml.AppendLine($"{key}:");
        foreach ((string oldValue, string newValue) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            yaml.AppendLine($"  {Quote(oldValue)}: {Quote(newValue)}");
        }
    }

    private static void WriteStringList(StringBuilder yaml, string key, IEnumerable<string> values)
    {
        string[] materialized = [.. values];
        if (materialized.Length == 0)
        {
            return;
        }

        yaml.AppendLine($"{key}:");
        foreach (string value in materialized)
        {
            yaml.AppendLine($"  - {Quote(value)}");
        }
    }

    private static void WritePolicies(StringBuilder yaml, ImmutableArray<PolicyAnnotation> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        yaml.AppendLine("policies:");
        foreach (PolicyAnnotation value in values)
        {
            yaml.AppendLine($"  - id: {Quote(value.Id)}");
            yaml.AppendLine($"    annotation: {Quote(value.Annotation)}");
        }
    }

    private static void WriteLearnedFields(StringBuilder yaml, ImmutableArray<LearnedFieldOverride> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        yaml.AppendLine("learnedFields:");
        foreach (LearnedFieldOverride value in values
                     .OrderBy(static item => item.DocumentKey, StringComparer.Ordinal)
                     .ThenBy(static item => item.SemanticPath, StringComparer.Ordinal))
        {
            yaml.AppendLine($"  - documentKey: {Quote(value.DocumentKey)}");
            yaml.AppendLine($"    semanticPath: {Quote(value.SemanticPath)}");
            yaml.AppendLine($"    value: {Quote(value.Value)}");
            yaml.AppendLine($"    valueSha256: {Quote(value.ValueSha256)}");
            yaml.AppendLine($"    botValueSha256: {Quote(value.BotValueSha256)}");
            yaml.AppendLine($"    sourceFingerprint: {Quote(value.SourceFingerprint)}");
            yaml.AppendLine($"    source: {Quote(value.Source)}");
            Scalar(yaml, "installerSelectorSha256", value.InstallerSelectorSha256, indentation: 4);
        }
    }

    private static void WriteQuirks(StringBuilder yaml, PackageQuirks quirks)
    {
        if (quirks.DisplayVersionFromEvidenceProperty is not { } property)
        {
            return;
        }

        yaml.AppendLine("quirks:");
        yaml.AppendLine($"  displayVersionFromEvidenceProperty: {Quote(property)}");
    }

    private static void Scalar(StringBuilder yaml, string key, string? value, int indentation = 0)
    {
        if (value is not null)
        {
            yaml.Append(' ', indentation).Append(key).Append(": ").AppendLine(Quote(value));
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            _ = character switch
            {
                '"' => result.Append("\\\""),
                '\\' => result.Append("\\\\"),
                '\n' => result.Append("\\n"),
                '\r' => result.Append("\\r"),
                '\t' => result.Append("\\t"),
                < ' ' or '\u0085' or '\u2028' or '\u2029' => result.Append($"\\u{(int)character:x4}"),
                _ => result.Append(character),
            };
        }

        return result.Append('"').ToString();
    }

    private sealed class Mapping
    {
        private readonly string _description;

        private Mapping(Dictionary<string, YamlNode> values, string description)
        {
            Values = values;
            _description = description;
        }

        public IReadOnlyDictionary<string, YamlNode> Values { get; }

        public static Mapping Create(YamlMappingNode mapping, string description)
        {
            var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
            {
                string key = ScalarValue(keyNode, $"a key in {description}");
                if (!values.TryAdd(key, valueNode))
                {
                    throw new FormatException($"Duplicate key '{key}' in {description}.");
                }
            }

            return new(values, description);
        }

        public void RequireKnownKeys(params string[] keys)
        {
            var known = new HashSet<string>(keys, StringComparer.Ordinal);
            foreach (string key in Values.Keys)
            {
                if (!known.Contains(key))
                {
                    throw new FormatException($"Unknown key '{key}' in {_description}.");
                }
            }
        }

        public string RequiredScalar(string key)
            => Values.TryGetValue(key, out YamlNode? value)
                ? ScalarValue(value, $"{_description}.{key}")
                : throw new FormatException($"Missing required key '{key}' in {_description}.");

        public string? OptionalScalar(string key)
            => Values.TryGetValue(key, out YamlNode? value) ? ScalarValue(value, $"{_description}.{key}") : null;

        public YamlMappingNode? OptionalMapping(string key)
            => Values.TryGetValue(key, out YamlNode? value)
                ? RequireMapping(value, $"{_description}.{key}")
                : null;

        public YamlSequenceNode? OptionalSequence(string key)
            => Values.TryGetValue(key, out YamlNode? value)
                ? value as YamlSequenceNode
                    ?? throw new FormatException($"'{_description}.{key}' must be a sequence.")
                : null;
    }
}
