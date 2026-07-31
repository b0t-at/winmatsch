using YamlDotNet.RepresentationModel;

namespace WinMatsch.Workflows.Configuration;

/// <summary>
/// Parses the user configuration file into a <see cref="ConfigurationLayer"/> using an explicit
/// representation-model walk (no reflection, AOT-safe). Unknown keys are rejected so typos fail
/// loudly instead of being silently ignored.
/// </summary>
public static class ConfigurationYamlParser
{
    /// <summary>Parses YAML text. An empty or whitespace-only document yields an empty layer.</summary>
    public static ConfigurationLayer Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return ConfigurationLayer.Empty;
        }

        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0)
        {
            return ConfigurationLayer.Empty;
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("The configuration document must be a YAML mapping.");
        }

        var layer = ConfigurationLayer.Empty;
        foreach ((YamlNode keyNode, YamlNode valueNode) in root.Children)
        {
            string key = GetScalar(keyNode, "a configuration key");
            layer = key switch
            {
                "repository" => layer with { Repository = GetScalar(valueNode, "repository") },
                "concurrentDownloads" => layer with
                {
                    ConcurrentDownloads = ConfigurationValues.ParseInt32(GetScalar(valueNode, "concurrentDownloads")),
                },
                "rules" => ParseRules(layer, valueNode),
                "cache" => ParseCache(layer, valueNode),
                "freshnessDelay" => layer with
                {
                    FreshnessDelay = ConfigurationValues.ParseFreshnessDelay(GetScalar(valueNode, "freshnessDelay")),
                },
                "output" => ParseOutput(layer, valueNode),
                "interaction" => layer with
                {
                    Interaction = ConfigurationValues.ParseInteractionMode(GetScalar(valueNode, "interaction")),
                },
                _ => throw new FormatException($"Unknown configuration key '{key}'."),
            };
        }

        return layer;
    }

    private static ConfigurationLayer ParseRules(ConfigurationLayer layer, YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode valueNode) in GetMapping(node, "rules").Children)
        {
            string key = GetScalar(keyNode, "a rules key");
            layer = key switch
            {
                "enabled" => layer with { EnabledRules = GetStringList(valueNode, "rules.enabled") },
                "disabled" => layer with { DisabledRules = GetStringList(valueNode, "rules.disabled") },
                _ => throw new FormatException($"Unknown configuration key 'rules.{key}'."),
            };
        }

        return layer;
    }

    private static ConfigurationLayer ParseCache(ConfigurationLayer layer, YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode valueNode) in GetMapping(node, "cache").Children)
        {
            string key = GetScalar(keyNode, "a cache key");
            layer = key switch
            {
                "enabled" => layer with
                {
                    CacheEnabled = ConfigurationValues.ParseBoolean(GetScalar(valueNode, "cache.enabled")),
                },
                "directory" => layer with { CacheDirectory = GetScalar(valueNode, "cache.directory") },
                _ => throw new FormatException($"Unknown configuration key 'cache.{key}'."),
            };
        }

        return layer;
    }

    private static ConfigurationLayer ParseOutput(ConfigurationLayer layer, YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode valueNode) in GetMapping(node, "output").Children)
        {
            string key = GetScalar(keyNode, "an output key");
            layer = key switch
            {
                "format" => layer with
                {
                    OutputFormat = ConfigurationValues.ParseOutputFormat(GetScalar(valueNode, "output.format")),
                },
                "directory" => layer with { OutputDirectory = GetScalar(valueNode, "output.directory") },
                _ => throw new FormatException($"Unknown configuration key 'output.{key}'."),
            };
        }

        return layer;
    }

    private static YamlMappingNode GetMapping(YamlNode node, string description)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw new FormatException($"'{description}' must be a mapping.");
        }

        return mapping;
    }

    private static string GetScalar(YamlNode node, string description)
    {
        if (node is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"'{description}' must be a non-empty scalar value.");
        }

        return value;
    }

    private static List<string> GetStringList(YamlNode node, string description)
    {
        if (node is not YamlSequenceNode sequence)
        {
            throw new FormatException($"'{description}' must be a sequence.");
        }

        var values = new List<string>(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
        {
            values.Add(GetScalar(child, $"an entry of {description}"));
        }

        return values;
    }
}
