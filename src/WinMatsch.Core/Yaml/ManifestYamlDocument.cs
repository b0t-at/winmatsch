using System.Buffers;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace WinMatsch.Core.Yaml;

/// <summary>
/// A manifest YAML document parsed under the resource limits used by every Core and Validation
/// entry point. The event pass runs before representation-tree construction so hostile depth,
/// node, scalar, tag, anchor, and alias shapes fail before recursive materialization.
/// </summary>
public sealed class ManifestYamlDocument
{
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    public const int MaxYamlEvents = 210_000;
    public const int MaxYamlDepth = 64;
    public const int MaxYamlNodes = 100_000;
    public const int MaxYamlScalars = 75_000;
    public const int MaxYamlTags = 10_000;

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private ManifestYamlDocument(
        string content,
        YamlMappingNode root,
        int byteCount,
        YamlResourceUsage resourceUsage)
    {
        Content = content;
        Root = root;
        ByteCount = byteCount;
        ResourceUsage = resourceUsage;
    }

    public string Content { get; }

    public YamlMappingNode Root { get; }

    internal int ByteCount { get; }

    internal YamlResourceUsage ResourceUsage { get; }

    public static ManifestYamlDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        int byteCount = Encoding.UTF8.GetByteCount(yaml);
        if (byteCount > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"A manifest cannot exceed {MaxManifestBytes} UTF-8 bytes.");
        }

        YamlResourceUsage resourceUsage = ValidateEvents(yaml);
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException(
                "A manifest must contain exactly one YAML mapping document.");
        }

        return new ManifestYamlDocument(yaml, root, byteCount, resourceUsage);
    }

    public static ManifestYamlDocument ReadFile(string path, string allowedRoot)
        => Parse(ReadTextFile(path, allowedRoot));

    /// <summary>
    /// Reads one UTF-8 manifest without whole-file convenience APIs, enforcing the byte limit and
    /// rejecting paths outside <paramref name="allowedRoot"/> or through reparse points.
    /// </summary>
    public static string ReadTextFile(string path, string allowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);

        string fullRoot = Path.GetFullPath(allowedRoot);
        string fullPath = Path.GetFullPath(path);
        EnsureContainedPath(fullRoot, fullPath);

        using SafeManifestFileLease lease = SafeManifestFile.OpenRead(fullRoot, fullPath);
        FileStream stream = lease.Stream;
        if (stream.Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"A manifest cannot exceed {MaxManifestBytes} UTF-8 bytes.");
        }

        int capacity = checked((int)Math.Min(stream.Length, MaxManifestBytes));
        using var content = new MemoryStream(capacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int total = 0;
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaxManifestBytes)
                {
                    throw new InvalidDataException(
                        $"A manifest cannot exceed {MaxManifestBytes} UTF-8 bytes.");
                }

                content.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ReadOnlySpan<byte> bytes = content.GetBuffer().AsSpan(0, total);
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return _strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A manifest must contain valid UTF-8.", exception);
        }
    }

    private static void EnsureContainedPath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Manifest path '{path}' must remain inside '{root}'.");
        }
    }

    private static YamlResourceUsage ValidateEvents(string yaml)
    {
        using var reader = new StringReader(yaml);
        var parser = new Parser(reader);
        int depth = 0;
        int events = 0;
        int nodes = 0;
        int scalars = 0;
        int tags = 0;
        while (parser.MoveNext())
        {
            events++;
            if (events > MaxYamlEvents)
            {
                throw new InvalidDataException(
                    $"A manifest cannot contain more than {MaxYamlEvents} YAML events.");
            }

            ParsingEvent parsingEvent = parser.Current
                ?? throw new InvalidDataException("YAML parser returned an empty event.");
            if (parsingEvent is AnchorAlias)
            {
                throw new InvalidDataException(
                    "YAML anchors and aliases are not permitted in manifests.");
            }

            if (parsingEvent is NodeEvent node)
            {
                nodes++;
                if (nodes > MaxYamlNodes)
                {
                    throw new InvalidDataException(
                        $"A manifest cannot contain more than {MaxYamlNodes} YAML nodes.");
                }

                if (!node.Anchor.IsEmpty)
                {
                    throw new InvalidDataException(
                        "YAML anchors and aliases are not permitted in manifests.");
                }

                if (!node.Tag.IsEmpty && !node.Tag.IsNonSpecific)
                {
                    tags++;
                    if (tags > MaxYamlTags)
                    {
                        throw new InvalidDataException(
                            $"A manifest cannot contain more than {MaxYamlTags} explicit YAML tags.");
                    }

                    ValidateTag(node, parsingEvent);
                }
            }

            if (parsingEvent is Scalar)
            {
                scalars++;
                if (scalars > MaxYamlScalars)
                {
                    throw new InvalidDataException(
                        $"A manifest cannot contain more than {MaxYamlScalars} YAML scalars.");
                }
            }

            depth += parsingEvent.NestingIncrease;
            if (depth > MaxYamlDepth)
            {
                throw new InvalidDataException(
                    $"YAML nesting cannot exceed {MaxYamlDepth} levels.");
            }
        }

        return new YamlResourceUsage(events, nodes, scalars, tags);
    }

    private static void ValidateTag(NodeEvent node, ParsingEvent parsingEvent)
    {
        string tag = node.Tag.Value;
        bool valid = parsingEvent switch
        {
            MappingStart => tag == "tag:yaml.org,2002:map",
            SequenceStart => tag == "tag:yaml.org,2002:seq",
            Scalar => tag is "tag:yaml.org,2002:str"
                or "tag:yaml.org,2002:null"
                or "tag:yaml.org,2002:bool"
                or "tag:yaml.org,2002:int"
                or "tag:yaml.org,2002:float",
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"YAML tag '{tag}' is unsupported or incompatible with node type '{parsingEvent.GetType().Name}'.");
        }
    }
}

internal readonly record struct YamlResourceUsage(
    int Events,
    int Nodes,
    int Scalars,
    int Tags);

/// <summary>Line-ending policy helpers for manifest serialization.</summary>
public static class ManifestYamlText
{
    /// <summary>
    /// Applies the line-ending style of an existing source file to canonical LF serialization.
    /// Existing CRLF files remain CRLF; existing LF files and newly generated files remain LF.
    /// Mixed existing input is normalized to CRLF when it contains any CRLF line ending.
    /// </summary>
    public static string PreserveExistingLineEndings(
        string canonicalYaml,
        string existingSource)
    {
        ArgumentNullException.ThrowIfNull(canonicalYaml);
        ArgumentNullException.ThrowIfNull(existingSource);
        return existingSource.Contains("\r\n", StringComparison.Ordinal)
            ? canonicalYaml.Replace("\n", "\r\n", StringComparison.Ordinal)
            : canonicalYaml;
    }
}

public sealed record ManifestYamlFile(string Path, ManifestYamlDocument Document);

/// <summary>Safely reads the bounded YAML files directly below one manifest directory.</summary>
public static class ManifestYamlDirectory
{
    public const int MaxDirectoryEntries = 4_096;
    public const int MaxManifestFiles = 256;
    public const long MaxTotalManifestBytes = 32L * 1024 * 1024;
    public const int MaxTotalYamlEvents = 420_000;
    public const int MaxTotalYamlNodes = 200_000;
    public const int MaxTotalYamlScalars = 150_000;
    public const int MaxTotalYamlTags = 20_000;

    public static IReadOnlyList<ManifestYamlFile> ReadFiles(
        string directoryPath,
        string allowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        string fullRoot = Path.GetFullPath(allowedRoot);
        string fullDirectory = Path.GetFullPath(directoryPath);
        EnsureContainedPath(fullRoot, fullDirectory);

        using SafeManifestDirectoryLease lease = SafeManifestFile.OpenDirectory(
            fullRoot,
            fullDirectory);
        var entries = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(lease.EnumerationPath))
        {
            if (entries.Count == MaxDirectoryEntries)
            {
                throw new InvalidDataException(
                    $"A manifest directory cannot contain more than {MaxDirectoryEntries} entries.");
            }

            entries.Add(Path.Combine(fullDirectory, Path.GetFileName(entry)));
        }

        string[] yamlPaths =
        [
            .. entries
                .Where(IsYamlFile)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal),
        ];
        if (yamlPaths.Length > MaxManifestFiles)
        {
            throw new InvalidDataException(
                $"A manifest directory cannot contain more than {MaxManifestFiles} YAML files.");
        }

        var files = new List<ManifestYamlFile>(yamlPaths.Length);
        long totalBytes = 0;
        int totalEvents = 0;
        int totalNodes = 0;
        int totalScalars = 0;
        int totalTags = 0;
        foreach (string path in yamlPaths)
        {
            ManifestYamlDocument document = ManifestYamlDocument.ReadFile(path, fullRoot);
            totalBytes = checked(totalBytes + document.ByteCount);
            totalEvents = checked(totalEvents + document.ResourceUsage.Events);
            totalNodes = checked(totalNodes + document.ResourceUsage.Nodes);
            totalScalars = checked(totalScalars + document.ResourceUsage.Scalars);
            totalTags = checked(totalTags + document.ResourceUsage.Tags);
            if (totalBytes > MaxTotalManifestBytes
                || totalEvents > MaxTotalYamlEvents
                || totalNodes > MaxTotalYamlNodes
                || totalScalars > MaxTotalYamlScalars
                || totalTags > MaxTotalYamlTags)
            {
                throw new InvalidDataException(
                    "The manifest set exceeds the aggregate YAML resource budget "
                    + $"({MaxTotalManifestBytes} bytes, {MaxTotalYamlEvents} events, "
                    + $"{MaxTotalYamlNodes} nodes, {MaxTotalYamlScalars} scalars, "
                    + $"{MaxTotalYamlTags} tags).");
            }

            files.Add(new ManifestYamlFile(path, document));
        }

        return files;
    }

    private static bool IsYamlFile(string path)
        => Path.GetExtension(path) is { } extension
            && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));

    private static void EnsureContainedPath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Manifest directory '{path}' must remain inside '{root}'.");
        }
    }
}
