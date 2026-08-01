using WinMatsch.Workflows.Configuration;
using Xunit;

namespace WinMatsch.Workflows.Tests.Configuration;

public class ConfigurationResolverTests
{
    [Fact]
    public void Defaults_apply_when_no_layer_sets_a_value()
    {
        WinMatschConfiguration configuration = ConfigurationResolver.Resolve();

        Assert.Equal("microsoft", configuration.Repository.Owner);
        Assert.Equal("winget-pkgs", configuration.Repository.Name);
        Assert.Equal(2, configuration.ConcurrentDownloads);
        Assert.Empty(configuration.EnabledRules);
        Assert.Empty(configuration.DisabledRules);
        Assert.True(configuration.CacheEnabled);
        Assert.Null(configuration.CacheDirectory);
        Assert.Equal(TimeSpan.FromHours(4), configuration.FreshnessDelay);
        Assert.Equal(OutputFormat.Text, configuration.OutputFormat);
        Assert.Null(configuration.OutputDirectory);
        Assert.Equal(InteractionMode.Auto, configuration.Interaction);
    }

    [Fact]
    public void Explicit_zero_freshness_delay_disables_the_default()
    {
        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(
            userConfiguration: new ConfigurationLayer { FreshnessDelay = TimeSpan.Zero });

        Assert.Equal(TimeSpan.Zero, configuration.FreshnessDelay);
    }

    [Fact]
    public void Command_layer_wins_over_all_others()
    {
        var command = new ConfigurationLayer { ConcurrentDownloads = 9, Repository = "cmd/repo" };
        var environment = new ConfigurationLayer { ConcurrentDownloads = 5, Repository = "env/repo" };
        var user = new ConfigurationLayer { ConcurrentDownloads = 3, Repository = "user/repo" };

        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(command, environment, user);

        Assert.Equal(9, configuration.ConcurrentDownloads);
        Assert.Equal("cmd/repo", configuration.Repository.ToString());
    }

    [Fact]
    public void Environment_layer_wins_over_user_configuration()
    {
        var environment = new ConfigurationLayer { OutputFormat = OutputFormat.Json };
        var user = new ConfigurationLayer { OutputFormat = OutputFormat.Text, CacheDirectory = "user-cache" };

        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(
            command: null,
            environment,
            user);

        Assert.Equal(OutputFormat.Json, configuration.OutputFormat);
        Assert.Equal("user-cache", configuration.CacheDirectory);
    }

    [Fact]
    public void User_configuration_wins_over_defaults()
    {
        var user = new ConfigurationLayer
        {
            CacheEnabled = false,
            FreshnessDelay = TimeSpan.FromHours(6),
            Interaction = InteractionMode.Never,
            EnabledRules = ["installer-url"],
        };

        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(userConfiguration: user);

        Assert.False(configuration.CacheEnabled);
        Assert.Equal(TimeSpan.FromHours(6), configuration.FreshnessDelay);
        Assert.Equal(InteractionMode.Never, configuration.Interaction);
        Assert.Equal(["installer-url"], configuration.EnabledRules);
    }

    [Fact]
    public void Each_field_falls_through_independently()
    {
        var command = new ConfigurationLayer { OutputDirectory = "cmd-out" };
        var environment = new ConfigurationLayer { ConcurrentDownloads = 6 };
        var user = new ConfigurationLayer { Repository = "user/repo" };

        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(command, environment, user);

        Assert.Equal("cmd-out", configuration.OutputDirectory);
        Assert.Equal(6, configuration.ConcurrentDownloads);
        Assert.Equal("user/repo", configuration.Repository.ToString());
        Assert.Equal(InteractionMode.Auto, configuration.Interaction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Concurrent_downloads_below_one_are_rejected(int value)
    {
        var command = new ConfigurationLayer { ConcurrentDownloads = value };

        Assert.Throws<FormatException>(() => ConfigurationResolver.Resolve(command));
    }

    [Fact]
    public void Negative_freshness_delay_is_rejected()
    {
        var command = new ConfigurationLayer { FreshnessDelay = TimeSpan.FromHours(-1) };

        Assert.Throws<FormatException>(() => ConfigurationResolver.Resolve(command));
    }

    [Fact]
    public void Invalid_repository_shapes_are_rejected()
    {
        var command = new ConfigurationLayer { Repository = "not-a-repo" };

        Assert.Throws<FormatException>(() => ConfigurationResolver.Resolve(command));
    }

    [Fact]
    public void Rule_ids_are_trimmed_and_empty_entries_rejected()
    {
        var trimmed = new ConfigurationLayer { EnabledRules = ["  installer-url  "] };
        Assert.Equal(["installer-url"], ConfigurationResolver.Resolve(trimmed).EnabledRules);

        var empty = new ConfigurationLayer { DisabledRules = ["   "] };
        Assert.Throws<FormatException>(() => ConfigurationResolver.Resolve(empty));
    }
}
