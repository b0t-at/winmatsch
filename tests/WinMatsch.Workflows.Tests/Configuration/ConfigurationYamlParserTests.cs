using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Workflows.Tests.Configuration;

public class ConfigurationYamlParserTests
{
    [Fact]
    public void Parse_reads_every_supported_key()
    {
        const string Yaml = """
            repository: contoso/packages
            concurrentDownloads: 4
            rules:
              enabled:
                - installer-url
                - manifest-schema
              disabled:
                - slow-check
            cache:
              enabled: false
              directory: C:/cache
            freshnessDelay: 2.00:00:00
            output:
              format: json
              directory: reports
            interaction: never
            """;

        ConfigurationLayer layer = ConfigurationYamlParser.Parse(Yaml);

        Assert.Equal("contoso/packages", layer.Repository);
        Assert.Equal(4, layer.ConcurrentDownloads);
        Assert.Equal(["installer-url", "manifest-schema"], layer.EnabledRules);
        Assert.Equal(["slow-check"], layer.DisabledRules);
        Assert.False(layer.CacheEnabled);
        Assert.Equal("C:/cache", layer.CacheDirectory);
        Assert.Equal(TimeSpan.FromDays(2), layer.FreshnessDelay);
        Assert.Equal(OutputFormat.Json, layer.OutputFormat);
        Assert.Equal("reports", layer.OutputDirectory);
        Assert.Equal(InteractionMode.Never, layer.Interaction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_documents_yield_an_empty_layer(string yaml)
    {
        Assert.Equal(ConfigurationLayer.Empty, ConfigurationYamlParser.Parse(yaml));
    }

    [Fact]
    public void Partial_documents_leave_other_values_unset()
    {
        ConfigurationLayer layer = ConfigurationYamlParser.Parse("repository: contoso/packages");

        Assert.Equal("contoso/packages", layer.Repository);
        Assert.Null(layer.ConcurrentDownloads);
        Assert.Null(layer.EnabledRules);
        Assert.Null(layer.CacheEnabled);
        Assert.Null(layer.OutputFormat);
    }

    [Fact]
    public void Unknown_top_level_keys_are_rejected()
    {
        FormatException exception = Assert.Throws<FormatException>(
            () => ConfigurationYamlParser.Parse("repositry: contoso/packages"));

        Assert.Contains("repositry", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rules:\n  enbled: []", "rules.enbled")]
    [InlineData("cache:\n  enable: true", "cache.enable")]
    [InlineData("output:\n  fmt: text", "output.fmt")]
    public void Unknown_nested_keys_are_rejected(string yaml, string expectedKey)
    {
        FormatException exception = Assert.Throws<FormatException>(() => ConfigurationYamlParser.Parse(yaml));

        Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("concurrentDownloads: many")]
    [InlineData("cache:\n  enabled: yes")]
    [InlineData("freshnessDelay: eventually")]
    [InlineData("freshnessDelay: -1.00:00:00")]
    [InlineData("output:\n  format: xml")]
    [InlineData("interaction: sometimes")]
    public void Invalid_scalar_values_are_rejected(string yaml)
    {
        Assert.Throws<FormatException>(() => ConfigurationYamlParser.Parse(yaml));
    }

    [Fact]
    public void Non_mapping_documents_are_rejected()
    {
        Assert.Throws<FormatException>(() => ConfigurationYamlParser.Parse("- just\n- a\n- list"));
    }

    [Fact]
    public void Rule_lists_must_be_sequences()
    {
        Assert.Throws<FormatException>(() => ConfigurationYamlParser.Parse("rules:\n  enabled: not-a-list"));
    }
}
