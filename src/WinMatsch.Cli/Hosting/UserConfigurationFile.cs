using WinMatsch.Workflows.Configuration;
using YamlDotNet.Core;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// Locates and loads the user configuration file layer.
/// An explicit <c>--config</c> path must exist; the default path
/// (<c>$XDG_CONFIG_HOME/winmatsch/config.yaml</c>, falling back to
/// <c>~/.config/winmatsch/config.yaml</c>) is optional and yields an empty layer when absent.
/// Parse failures surface as <see cref="FormatException"/> prefixed with the file path, which
/// the host maps to <see cref="ExitCodes.ConfigurationError"/>.
/// </summary>
public static class UserConfigurationFile
{
    /// <summary>The configuration file name inside the winmatsch configuration directory.</summary>
    public const string FileName = "config.yaml";

    /// <summary>The directory name under the platform configuration root.</summary>
    public const string DirectoryName = "winmatsch";

    public static ConfigurationLayer Load(
        string? explicitPath,
        Func<string, string?> environment,
        Func<string, string?> readTextFile,
        string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readTextFile);

        string? path = explicitPath ?? GetDefaultPath(environment, homeDirectory);
        if (path is null)
        {
            return ConfigurationLayer.Empty;
        }

        string? content = readTextFile(path);
        if (content is null)
        {
            if (explicitPath is not null)
            {
                throw new FileNotFoundException(
                    $"The configuration file '{explicitPath}' was not found.", explicitPath);
            }

            return ConfigurationLayer.Empty;
        }

        try
        {
            return ConfigurationYamlParser.Parse(content);
        }
        catch (Exception exception) when (exception is FormatException or YamlException)
        {
            throw new FormatException($"{path}: {exception.Message}", exception);
        }
    }

    /// <summary>The default path, or null when no home directory is known.</summary>
    public static string? GetDefaultPath(Func<string, string?> environment, string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string? xdgConfigHome = environment("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, DirectoryName, FileName);
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return null;
        }

        return Path.Combine(homeDirectory, ".config", DirectoryName, FileName);
    }
}
