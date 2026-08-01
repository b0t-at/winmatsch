using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WinMatsch.Cli.Hosting;
using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>File access seam for the configuration commands, injectable for tests.</summary>
public interface IConfigFileSystem
{
    /// <summary>Reads a text file, or returns null when it does not exist.</summary>
    public string? ReadText(string path);

    /// <summary>
    /// Writes a text file atomically: the content lands in a temporary sibling first and is
    /// moved into place, so a failure never leaves a truncated configuration file. On Unix the
    /// file is created owner-readable only.
    /// </summary>
    public void WriteTextAtomic(string path, string content);
}

/// <summary>The production <see cref="IConfigFileSystem"/> over the real file system.</summary>
public sealed class ConfigFileSystem : IConfigFileSystem
{
    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteTextAtomic(string path, string content)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException($"The configuration path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}

/// <summary>
/// Reads and edits the user configuration file: <c>config show</c> (effective values with
/// per-key provenance), <c>config set</c> / <c>config unset</c> (deterministic YAML, atomic
/// write, known keys only), and <c>config path</c>. Tokens are never configuration; any
/// attempt to store one here is rejected with a pointer to <c>token add</c>.
/// </summary>
public sealed class ConfigCommandModule : ICommandModule
{
    private static readonly string[] _knownKeys =
    [
        "repository",
        "concurrentDownloads",
        "rules.enabled",
        "rules.disabled",
        "cache.enabled",
        "cache.directory",
        "freshnessDelay",
        "output.format",
        "output.directory",
        "interaction",
    ];

    private static readonly string[] _secretKeys = ["token", "github_token", "githubToken"];

    private readonly Func<string, string?> _environment;
    private readonly string? _homeDirectory;
    private readonly IConfigFileSystem _fileSystem;

    public ConfigCommandModule(
        Func<string, string?>? environment = null,
        string? homeDirectory = null,
        IConfigFileSystem? fileSystem = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _homeDirectory = homeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _fileSystem = fileSystem ?? new ConfigFileSystem();
    }

    public string Name => "config";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var command = new Command("config", "Inspect and edit the user configuration file.");
        command.Subcommands.Add(CreateShow(registry));
        command.Subcommands.Add(CreateSet(registry));
        command.Subcommands.Add(CreateUnset(registry));
        command.Subcommands.Add(CreatePath(registry));
        registry.AddCommand(command);
    }

    private Command CreateShow(ICommandRegistry registry)
    {
        var command = new Command(
            "show",
            "Show the effective configuration and where each value comes from "
            + "(command > environment > user file > default). Tokens are never configuration.");
        registry.SetHandler(command, context =>
        {
            ConfigurationLayer commandLayer = registry.GlobalOptions.BindConfigurationLayer(context.ParseResult);
            ConfigurationLayer environmentLayer = EnvironmentConfiguration.Read(_environment);
            ConfigurationLayer userLayer = LoadUserLayer(context, registry);
            WinMatschConfiguration effective = context.Configuration;

            var entries = new List<(string Key, string? Value, string Source)>
            {
                ("repository",
                    effective.Repository.ToString(),
                    Provenance(commandLayer.Repository, environmentLayer.Repository, userLayer.Repository)),
                ("concurrentDownloads",
                    effective.ConcurrentDownloads.ToString(CultureInfo.InvariantCulture),
                    Provenance(commandLayer.ConcurrentDownloads, environmentLayer.ConcurrentDownloads, userLayer.ConcurrentDownloads)),
                ("rules.enabled",
                    FormatList(effective.EnabledRules),
                    Provenance(commandLayer.EnabledRules, environmentLayer.EnabledRules, userLayer.EnabledRules)),
                ("rules.disabled",
                    FormatList(effective.DisabledRules),
                    Provenance(commandLayer.DisabledRules, environmentLayer.DisabledRules, userLayer.DisabledRules)),
                ("cache.enabled",
                    effective.CacheEnabled ? "true" : "false",
                    Provenance(commandLayer.CacheEnabled, environmentLayer.CacheEnabled, userLayer.CacheEnabled)),
                ("cache.directory",
                    effective.CacheDirectory,
                    Provenance(commandLayer.CacheDirectory, environmentLayer.CacheDirectory, userLayer.CacheDirectory)),
                ("freshnessDelay",
                    effective.FreshnessDelay.ToString("c", CultureInfo.InvariantCulture),
                    Provenance(commandLayer.FreshnessDelay, environmentLayer.FreshnessDelay, userLayer.FreshnessDelay)),
                ("output.format",
                    MaintenanceCommandHelpers.ToCamelCase(effective.OutputFormat),
                    Provenance(commandLayer.OutputFormat, environmentLayer.OutputFormat, userLayer.OutputFormat)),
                ("output.directory",
                    effective.OutputDirectory,
                    Provenance(commandLayer.OutputDirectory, environmentLayer.OutputDirectory, userLayer.OutputDirectory)),
                ("interaction",
                    MaintenanceCommandHelpers.ToCamelCase(effective.Interaction),
                    Provenance(commandLayer.Interaction, environmentLayer.Interaction, userLayer.Interaction)),
            };

            context.Output.WriteFormatted(
                writer =>
                {
                    foreach ((string key, string? value, string source) in entries)
                    {
                        writer.WriteLine($"{key} = {value ?? "(not set)"} [{source}]");
                    }
                },
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("settings");
                    foreach ((string key, string? value, string source) in entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("key", key);
                        if (value is null)
                        {
                            writer.WriteNull("value");
                        }
                        else
                        {
                            writer.WriteString("value", value);
                        }

                        writer.WriteString("source", source);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private Command CreateSet(ICommandRegistry registry)
    {
        var key = new Argument<string>("key")
        {
            Description = "Configuration key (one of: " + string.Join(", ", _knownKeys) + ").",
        };
        var value = new Argument<string>("value")
        {
            Description = "The value to store. Validated with the same rules the CLI applies at startup.",
        };
        var command = new Command("set", "Set one configuration key in the user configuration file.")
        {
            Arguments = { key, value },
        };
        registry.SetHandler(command, context =>
        {
            string keyName = RequireKnownKey(context.ParseResult.GetValue(key));
            string rawValue = context.ParseResult.GetValue(value)
                ?? throw new CliUsageException("A value is required.");
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                throw new CliUsageException("The value must not be empty.");
            }

            string path = ResolvePath(context, registry).Path;
            ConfigurationLayer layer = LoadUserLayer(context, registry);
            layer = ApplyKey(layer, keyName, rawValue);
            string yaml = SerializeDeterministic(layer);
            ValidateRoundTrip(yaml, keyName, rawValue);
            ValidateResolvable(layer, keyName, rawValue);
            if (context.IsDryRun)
            {
                WriteMutationResult(context, "set", keyName, rawValue, path, applied: false);
                return Task.FromResult(ExitCodes.Success);
            }

            WriteAtomic(path, yaml);
            WriteMutationResult(context, "set", keyName, rawValue, path, applied: true);
            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private Command CreateUnset(ICommandRegistry registry)
    {
        var key = new Argument<string>("key")
        {
            Description = "Configuration key (one of: " + string.Join(", ", _knownKeys) + ").",
        };
        var command = new Command(
            "unset",
            "Remove one configuration key from the user configuration file. Succeeds when the key is absent.")
        {
            Arguments = { key },
        };
        registry.SetHandler(command, context =>
        {
            string keyName = RequireKnownKey(context.ParseResult.GetValue(key));
            string path = ResolvePath(context, registry).Path;
            ConfigurationLayer layer = LoadUserLayer(context, registry);
            layer = ApplyKey(layer, keyName, null);
            string yaml = SerializeDeterministic(layer);
            if (context.IsDryRun)
            {
                WriteMutationResult(context, "unset", keyName, null, path, applied: false);
                return Task.FromResult(ExitCodes.Success);
            }

            WriteAtomic(path, yaml);
            WriteMutationResult(context, "unset", keyName, null, path, applied: true);
            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private Command CreatePath(ICommandRegistry registry)
    {
        var command = new Command(
            "path",
            "Print the user configuration file path in effect for this invocation.");
        registry.SetHandler(command, context =>
        {
            (string path, string source) = ResolvePath(context, registry);
            bool exists = _fileSystem.ReadText(path) is not null;
            context.Output.WriteFormatted(
                writer =>
                {
                    writer.WriteLine(path);
                    writer.WriteLine($"Source: {source}");
                    writer.WriteLine($"Exists: {(exists ? "yes" : "no")}");
                },
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", path);
                    writer.WriteString("source", source);
                    writer.WriteBoolean("exists", exists);
                    writer.WriteEndObject();
                });
            return Task.FromResult(ExitCodes.Success);
        });
        return command;
    }

    private (string Path, string Source) ResolvePath(CommandContext context, ICommandRegistry registry)
    {
        string? explicitPath = context.ParseResult.GetValue(registry.GlobalOptions.ConfigFile);
        if (explicitPath is not null)
        {
            return (explicitPath, "explicit");
        }

        string? defaultPath = UserConfigurationFile.GetDefaultPath(_environment, _homeDirectory);
        if (defaultPath is null)
        {
            throw new CliOperationException(
                "No configuration file path could be resolved: no home directory is known. "
                + "Pass --config with an explicit path.");
        }

        return (defaultPath, "default");
    }

    private ConfigurationLayer LoadUserLayer(CommandContext context, ICommandRegistry registry)
    {
        // Parse failures propagate as FormatException, which the host maps to the
        // configuration-error exit code — a malformed user file is a configuration problem.
        (string path, _) = ResolvePath(context, registry);
        string? content = _fileSystem.ReadText(path);
        if (content is null)
        {
            return ConfigurationLayer.Empty;
        }

        try
        {
            return ConfigurationYamlParser.Parse(content);
        }
        catch (Exception exception) when (exception is FormatException or YamlDotNet.Core.YamlException)
        {
            throw new FormatException($"{path}: {exception.Message}", exception);
        }
    }

    private void WriteAtomic(string path, string content)
    {
        try
        {
            _fileSystem.WriteTextAtomic(path, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliOperationException(
                $"The configuration file '{path}' could not be written: {exception.Message} "
                + "The previous configuration is unchanged.",
                exception);
        }
    }

    private static string RequireKnownKey(string? value)
    {
        string keyName = value ?? throw new CliUsageException("A configuration key is required.");
        if (_secretKeys.Contains(keyName, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                "Tokens are never stored in the configuration file. Use 'winmatsch token add' "
                + "or the GITHUB_TOKEN environment variable instead.");
        }

        if (!_knownKeys.Contains(keyName, StringComparer.Ordinal))
        {
            string? nearMatch = _knownKeys.FirstOrDefault(known =>
                string.Equals(known, keyName, StringComparison.OrdinalIgnoreCase));
            throw new CliUsageException(nearMatch is null
                ? $"Unknown configuration key '{keyName}'. Known keys: {string.Join(", ", _knownKeys)}."
                : $"Unknown configuration key '{keyName}'. Did you mean '{nearMatch}'? Keys are case-sensitive.");
        }

        return keyName;
    }

    private static ConfigurationLayer ApplyKey(ConfigurationLayer layer, string key, string? value)
        => key switch
        {
            "repository" => layer with { Repository = value },
            "concurrentDownloads" => layer with
            {
                ConcurrentDownloads = value is null ? null : ParseInt(value),
            },
            "rules.enabled" => layer with { EnabledRules = SplitList(value) },
            "rules.disabled" => layer with { DisabledRules = SplitList(value) },
            "cache.enabled" => layer with { CacheEnabled = value is null ? null : ParseBool(value) },
            "cache.directory" => layer with { CacheDirectory = value },
            "freshnessDelay" => layer with
            {
                FreshnessDelay = value is null ? null : ParseDelay(value),
            },
            "output.format" => layer with
            {
                OutputFormat = value is null ? null : ParseFormat(value),
            },
            "output.directory" => layer with { OutputDirectory = value },
            "interaction" => layer with
            {
                Interaction = value is null ? null : ParseInteraction(value),
            },
            _ => throw new CliUsageException($"Unknown configuration key '{key}'."),
        };

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new CliUsageException($"'{value}' is not a valid integer.");

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => throw new CliUsageException($"'{value}' is not a valid boolean. Use 'true' or 'false'."),
    };

    private static TimeSpan ParseDelay(string value)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            || parsed < TimeSpan.Zero)
        {
            throw new CliUsageException(
                $"'{value}' is not a valid non-negative time span. Use a format like '2.00:00:00'.");
        }

        return parsed;
    }

    private static OutputFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        _ => throw new CliUsageException($"'{value}' is not a valid output format. Use 'text' or 'json'."),
    };

    private static InteractionMode ParseInteraction(string value) => value.Trim().ToLowerInvariant() switch
    {
        "auto" => InteractionMode.Auto,
        "always" => InteractionMode.Always,
        "never" => InteractionMode.Never,
        _ => throw new CliUsageException(
            $"'{value}' is not a valid interaction mode. Use 'auto', 'always', or 'never'."),
    };

    private static string[]? SplitList(string? value)
        => value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Serializes a configuration layer as deterministic YAML: fixed key order, double-quoted
    /// strings with escaping, invariant scalar formatting, LF line endings.
    /// </summary>
    internal static string SerializeDeterministic(ConfigurationLayer layer)
    {
        var builder = new StringBuilder();
        AppendScalar(builder, 0, "repository", layer.Repository);
        if (layer.ConcurrentDownloads is { } concurrent)
        {
            builder.Append("concurrentDownloads: ")
                .Append(concurrent.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        if (layer.EnabledRules is { Count: > 0 } || layer.DisabledRules is { Count: > 0 })
        {
            builder.Append("rules:\n");
            AppendSequence(builder, "enabled", layer.EnabledRules);
            AppendSequence(builder, "disabled", layer.DisabledRules);
        }

        if (layer.CacheEnabled is not null || layer.CacheDirectory is not null)
        {
            builder.Append("cache:\n");
            if (layer.CacheEnabled is { } cacheEnabled)
            {
                builder.Append("  enabled: ").Append(cacheEnabled ? "true" : "false").Append('\n');
            }

            AppendScalar(builder, 2, "directory", layer.CacheDirectory);
        }

        AppendScalar(
            builder,
            0,
            "freshnessDelay",
            layer.FreshnessDelay?.ToString("c", CultureInfo.InvariantCulture));
        if (layer.OutputFormat is not null || layer.OutputDirectory is not null)
        {
            builder.Append("output:\n");
            AppendScalar(
                builder,
                2,
                "format",
                layer.OutputFormat is { } format ? MaintenanceCommandHelpers.ToCamelCase(format) : null);
            AppendScalar(builder, 2, "directory", layer.OutputDirectory);
        }

        AppendScalar(
            builder,
            0,
            "interaction",
            layer.Interaction is { } interaction ? MaintenanceCommandHelpers.ToCamelCase(interaction) : null);
        return builder.ToString();
    }

    private static void AppendScalar(StringBuilder builder, int indent, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        builder.Append(' ', indent).Append(key).Append(": ").Append(Quote(value)).Append('\n');
    }

    private static void AppendSequence(StringBuilder builder, string key, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        builder.Append("  ").Append(key).Append(":\n");
        foreach (string value in values)
        {
            builder.Append("    - ").Append(Quote(value)).Append('\n');
        }
    }

    /// <summary>Double-quotes a YAML scalar, escaping backslashes and quotes; casing is preserved.</summary>
    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static void ValidateRoundTrip(string yaml, string key, string value)
    {
        try
        {
            _ = ConfigurationYamlParser.Parse(yaml);
        }
        catch (Exception exception) when (
            exception is FormatException or YamlDotNet.Core.YamlException)
        {
            throw new CliUsageException(
                $"'{value}' is not a valid value for '{key}': {exception.Message}",
                exception);
        }
    }

    /// <summary>Rejects values the runtime resolver would refuse (bad repository, ranges, ...).</summary>
    private static void ValidateResolvable(ConfigurationLayer layer, string key, string value)
    {
        try
        {
            _ = ConfigurationResolver.Resolve(userConfiguration: layer);
        }
        catch (FormatException exception)
        {
            throw new CliUsageException(
                $"'{value}' is not a valid value for '{key}': {exception.Message}",
                exception);
        }
    }

    private static void WriteMutationResult(
        CommandContext context,
        string action,
        string key,
        string? value,
        string path,
        bool applied)
        => context.Output.WriteFormatted(
            writer => writer.WriteLine((action, applied) switch
            {
                ("set", true) => $"Set {key} = {value} in {path}.",
                ("set", false) => $"Would set {key} = {value} in {path} (dry run; nothing was written).",
                (_, true) => $"Removed {key} from {path}.",
                (_, false) => $"Would remove {key} from {path} (dry run; nothing was written).",
            }),
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("action", action);
                writer.WriteString("key", key);
                if (value is null)
                {
                    writer.WriteNull("value");
                }
                else
                {
                    writer.WriteString("value", value);
                }

                writer.WriteString("path", path);
                writer.WriteBoolean("applied", applied);
                writer.WriteEndObject();
            });

    private static string? FormatList(IReadOnlyList<string> values)
        => values.Count == 0 ? null : string.Join(", ", values);

    private static string Provenance<T>(T? command, T? environment, T? user)
        where T : class
        => command is not null ? "command"
            : environment is not null ? "environment"
            : user is not null ? "user"
            : "default";

    private static string Provenance<T>(T? command, T? environment, T? user)
        where T : struct
        => command.HasValue ? "command"
            : environment.HasValue ? "environment"
            : user.HasValue ? "user"
            : "default";
}
