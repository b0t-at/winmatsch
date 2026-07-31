using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Workflows.Tests.Configuration;

public class EnvironmentConfigurationTests
{
    [Fact]
    public void Read_maps_every_supported_variable()
    {
        var variables = new Dictionary<string, string>
        {
            ["WINMATSCH_REPOSITORY"] = "contoso/packages",
            ["WINMATSCH_CONCURRENT_DOWNLOADS"] = "8",
            ["WINMATSCH_RULES_ENABLED"] = "installer-url, manifest-schema",
            ["WINMATSCH_RULES_DISABLED"] = "slow-check",
            ["WINMATSCH_CACHE_ENABLED"] = "false",
            ["WINMATSCH_CACHE_DIRECTORY"] = "C:/cache",
            ["WINMATSCH_FRESHNESS_DELAY"] = "1.12:00:00",
            ["WINMATSCH_OUTPUT_FORMAT"] = "json",
            ["WINMATSCH_OUTPUT_DIRECTORY"] = "reports",
            ["WINMATSCH_INTERACTION"] = "never",
        };

        ConfigurationLayer layer = EnvironmentConfiguration.Read(name => variables.GetValueOrDefault(name));

        Assert.Equal("contoso/packages", layer.Repository);
        Assert.Equal(8, layer.ConcurrentDownloads);
        Assert.Equal(["installer-url", "manifest-schema"], layer.EnabledRules);
        Assert.Equal(["slow-check"], layer.DisabledRules);
        Assert.False(layer.CacheEnabled);
        Assert.Equal("C:/cache", layer.CacheDirectory);
        Assert.Equal(new TimeSpan(1, 12, 0, 0), layer.FreshnessDelay);
        Assert.Equal(OutputFormat.Json, layer.OutputFormat);
        Assert.Equal("reports", layer.OutputDirectory);
        Assert.Equal(InteractionMode.Never, layer.Interaction);
    }

    [Fact]
    public void Unset_variables_produce_an_empty_layer()
    {
        ConfigurationLayer layer = EnvironmentConfiguration.Read(_ => null);

        Assert.Equal(ConfigurationLayer.Empty, layer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_and_whitespace_variables_are_treated_as_unset(string value)
    {
        ConfigurationLayer layer = EnvironmentConfiguration.Read(_ => value);

        Assert.Equal(ConfigurationLayer.Empty, layer);
    }

    [Fact]
    public void Invalid_values_report_the_variable_name()
    {
        var variables = new Dictionary<string, string>
        {
            ["WINMATSCH_CONCURRENT_DOWNLOADS"] = "many",
        };

        FormatException exception = Assert.Throws<FormatException>(
            () => EnvironmentConfiguration.Read(name => variables.GetValueOrDefault(name)));

        Assert.Contains("WINMATSCH_CONCURRENT_DOWNLOADS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WINMATSCH_CACHE_ENABLED", "yes")]
    [InlineData("WINMATSCH_FRESHNESS_DELAY", "eventually")]
    [InlineData("WINMATSCH_OUTPUT_FORMAT", "xml")]
    [InlineData("WINMATSCH_INTERACTION", "sometimes")]
    public void Invalid_typed_values_are_rejected(string variable, string value)
    {
        Assert.Throws<FormatException>(
            () => EnvironmentConfiguration.Read(name => name == variable ? value : null));
    }
}
