using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Downloads;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Inspects and maintains the persistent download cache through the bounded
/// <see cref="DownloadCache"/> API only: <c>cache list</c>, <c>cache inspect</c>,
/// <c>cache clear</c>, and <c>cache prune</c>. Entries are addressed exclusively by URL —
/// no file paths are ever accepted — and every destructive action supports dry-run and
/// requires explicit confirmation. All operations serialize through the cache's own
/// cross-process lock, so they are safe next to concurrent downloads.
/// </summary>
public sealed class CacheCommandModule : ICommandModule
{
    private readonly Func<string, DownloadCache> _cacheFactory;

    public CacheCommandModule(Func<string, DownloadCache>? cacheFactory = null)
    {
        _cacheFactory = cacheFactory ?? (static directory => new DownloadCache(directory));
    }

    public string Name => "cache";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var command = new Command("cache", "Inspect and maintain the persistent download cache.");
        command.Subcommands.Add(CreateList(registry));
        command.Subcommands.Add(CreateInspect(registry));
        command.Subcommands.Add(CreateClear(registry));
        command.Subcommands.Add(CreatePrune(registry));
        registry.AddCommand(command);
    }

    /// <summary>The cache directory in effect: configured, or the platform default.</summary>
    internal static string ResolveDirectory(CommandContext context)
        => context.Configuration.CacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "winmatsch",
                "downloads");

    private Command CreateList(ICommandRegistry registry)
    {
        var command = new Command(
            "list",
            "List all cache entries with integrity state, size, and lifetime information.");
        registry.SetHandler(command, async context =>
        {
            DownloadCache cache = CreateCache(context);
            IReadOnlyList<DownloadCacheEntryInfo> entries = await InspectCacheAsync(context, cache)
                .ConfigureAwait(false);
            WriteEntries(context, cache.DirectoryPath, entries);
            return ExitCodes.Success;
        });
        return command;
    }

    private Command CreateInspect(ICommandRegistry registry)
    {
        var url = CreateUrlArgument();
        var command = new Command(
            "inspect",
            "Show integrity, size, and lifetime details of the cache entry for one URL.")
        {
            Arguments = { url },
        };
        registry.SetHandler(command, async context =>
        {
            string urlValue = RequireUrl(context.ParseResult.GetValue(url));
            DownloadCache cache = CreateCache(context);
            IReadOnlyList<DownloadCacheEntryInfo> entries = await InspectCacheAsync(context, cache)
                .ConfigureAwait(false);
            DownloadCacheEntryInfo? entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Url, urlValue, StringComparison.Ordinal));
            if (entry is null)
            {
                throw new CliOperationException($"No cache entry exists for '{RedactUrl(urlValue)}'.");
            }

            context.Output.WriteFormatted(
                writer => WriteEntryText(writer, entry, indent: ""),
                writer => WriteEntryJson(writer, entry));
            return ExitCodes.Success;
        });
        return command;
    }

    private Command CreateClear(ICommandRegistry registry)
    {
        var url = new Argument<string?>("url")
        {
            Description = "Clear only the entry for this URL. Omit to clear every entry.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var yes = CreateYesOption();
        var command = new Command(
            "clear",
            "Remove one cache entry, or the whole cache. Destructive; supports --dry-run.")
        {
            Arguments = { url },
            Options = { yes },
        };
        registry.SetHandler(command, async context =>
        {
            string? urlValue = context.ParseResult.GetValue(url);
            if (urlValue is not null)
            {
                urlValue = RequireUrl(urlValue);
            }

            DownloadCache cache = CreateCache(context);
            IReadOnlyList<DownloadCacheEntryInfo> entries = await InspectCacheAsync(context, cache)
                .ConfigureAwait(false);
            IReadOnlyList<DownloadCacheEntryInfo> targets = urlValue is null
                ? entries
                : [.. entries.Where(entry => string.Equals(entry.Url, urlValue, StringComparison.Ordinal))];
            IReadOnlyList<string> plannedFiles = urlValue is null
                ? InspectCacheFiles(cache.DirectoryPath)
                : [];

            if (context.IsDryRun)
            {
                WriteMutationPlan(
                    context,
                    cache.DirectoryPath,
                    "clear",
                    targets,
                    applied: false,
                    plannedFiles: plannedFiles);
                return ExitCodes.Success;
            }

            if (urlValue is null || targets.Count > 0)
            {
                bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                    context,
                    context.ParseResult.GetValue(yes),
                    urlValue is null
                        ? targets.Count == 0
                            ? "Remove all artifacts, including orphaned files, from the download cache?"
                            : $"Remove all {targets.Count} entr{(targets.Count == 1 ? "y" : "ies")} "
                                + "and any orphaned files from the download cache?"
                        : $"Remove the cache entry for '{RedactUrl(urlValue)}'?")
                    .ConfigureAwait(false);
                if (!confirmed)
                {
                    context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was removed.");
                    return ExitCodes.OperationFailed;
                }

                await RunCacheAsync(
                    context,
                    () => cache.ClearAsync(urlValue, context.CancellationToken))
                    .ConfigureAwait(false);
            }

            WriteMutationPlan(
                context,
                cache.DirectoryPath,
                "clear",
                targets,
                applied: true,
                plannedFiles: plannedFiles);
            return ExitCodes.Success;
        });
        return command;
    }

    private Command CreatePrune(ICommandRegistry registry)
    {
        var yes = CreateYesOption();
        var command = new Command(
            "prune",
            "Remove stale and corrupt cache entries, keeping fresh ones. Destructive; supports --dry-run.")
        {
            Options = { yes },
        };
        registry.SetHandler(command, async context =>
        {
            DownloadCache cache = CreateCache(context);
            IReadOnlyList<DownloadCacheEntryInfo> entries = await InspectCacheAsync(context, cache)
                .ConfigureAwait(false);
            IReadOnlyList<DownloadCacheEntryInfo> removable =
            [
                .. entries.Where(static entry =>
                    entry.State != DownloadCacheEntryState.Fresh && entry.Url.Length > 0),
            ];
            IReadOnlyList<DownloadCacheEntryInfo> unaddressable =
            [
                .. entries.Where(static entry =>
                    entry.State != DownloadCacheEntryState.Fresh && entry.Url.Length == 0),
            ];

            if (context.IsDryRun)
            {
                WriteMutationPlan(context, cache.DirectoryPath, "prune", removable, applied: false, unaddressable);
                return ExitCodes.Success;
            }

            if (removable.Count > 0)
            {
                bool confirmed = await MaintenanceCommandHelpers.ConfirmMutationAsync(
                    context,
                    context.ParseResult.GetValue(yes),
                    $"Remove {removable.Count} stale or corrupt cache entr{(removable.Count == 1 ? "y" : "ies")}?")
                    .ConfigureAwait(false);
                if (!confirmed)
                {
                    context.Output.WriteDiagnostic("Aborted: confirmation declined; nothing was removed.");
                    return ExitCodes.OperationFailed;
                }

                // Re-inspect after the confirmation gap and keep only entries that are still
                // not fresh, so an entry refreshed by a concurrent download survives. The
                // remaining window between this check and each per-URL delete is inherent to
                // the bounded per-operation cache lock.
                IReadOnlyList<DownloadCacheEntryInfo> recheck = await InspectCacheAsync(context, cache)
                    .ConfigureAwait(false);
                var stillRemovableUrls = recheck
                    .Where(static entry => entry.State != DownloadCacheEntryState.Fresh && entry.Url.Length > 0)
                    .Select(static entry => entry.Url)
                    .ToHashSet(StringComparer.Ordinal);
                removable = [.. removable.Where(entry => stillRemovableUrls.Contains(entry.Url))];
                foreach (DownloadCacheEntryInfo entry in removable)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await RunCacheAsync(
                        context,
                        () => cache.ClearAsync(entry.Url, context.CancellationToken))
                        .ConfigureAwait(false);
                }
            }

            WriteMutationPlan(context, cache.DirectoryPath, "prune", removable, applied: true, unaddressable);
            return ExitCodes.Success;
        });
        return command;
    }

    private DownloadCache CreateCache(CommandContext context)
    {
        try
        {
            return _cacheFactory(ResolveDirectory(context));
        }
        catch (ArgumentException exception)
        {
            throw new CliOperationException($"The cache directory is invalid: {exception.Message}", exception);
        }
    }

    private static async Task<IReadOnlyList<DownloadCacheEntryInfo>> InspectCacheAsync(
        CommandContext context,
        DownloadCache cache)
        => await RunCacheAsync(context, () => cache.InspectAsync(context.CancellationToken))
            .ConfigureAwait(false);

    private static Task<T> RunCacheAsync<T>(CommandContext context, Func<Task<T>> operation)
        => MaintenanceCommandHelpers.RunRemoteAsync(context, "Cache access failed", operation);

    private static async Task RunCacheAsync(CommandContext context, Func<Task> operation)
        => _ = await MaintenanceCommandHelpers.RunRemoteAsync(
            context,
            "Cache access failed",
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

    private static Argument<string> CreateUrlArgument() => new("url")
    {
        Description = "The exact installer URL the entry was cached under.",
    };

    private static Option<bool> CreateYesOption() => new("--yes")
    {
        Description = "Confirm the removal without prompting. Required in non-interactive "
            + "and JSON sessions; confirmation never defaults to yes.",
    };

    private static string RequireUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException("A non-empty URL is required.");
        }

        // Entries are keyed by installer URL only; rejecting anything else keeps option-like
        // tokens and path-shaped input out of the cache surface entirely.
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CliUsageException("The cache entry URL must be an absolute http(s) URL.");
        }

        return value;
    }

    private static void WriteEntries(
        CommandContext context,
        string directory,
        IReadOnlyList<DownloadCacheEntryInfo> entries)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Cache directory: {directory}");
                writer.WriteLine($"Entries: {entries.Count.ToString(CultureInfo.InvariantCulture)}");
                foreach (DownloadCacheEntryInfo entry in entries)
                {
                    writer.WriteLine(
                        $"  [{MaintenanceCommandHelpers.ToCamelCase(entry.State)}] "
                        + $"{FormatSize(entry)} {FormatUrl(entry)}");
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("directory", directory);
                writer.WriteStartArray("entries");
                foreach (DownloadCacheEntryInfo entry in entries)
                {
                    WriteEntryJson(writer, entry);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });

    private static void WriteMutationPlan(
        CommandContext context,
        string directory,
        string action,
        IReadOnlyList<DownloadCacheEntryInfo> targets,
        bool applied,
        IReadOnlyList<DownloadCacheEntryInfo>? unaddressable = null,
        IReadOnlyList<string>? plannedFiles = null)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Cache directory: {directory}");
                writer.WriteLine(applied
                    ? $"Removed {targets.Count.ToString(CultureInfo.InvariantCulture)} entr{(targets.Count == 1 ? "y" : "ies")}."
                    : $"Would remove {targets.Count.ToString(CultureInfo.InvariantCulture)} entr{(targets.Count == 1 ? "y" : "ies")} (dry run; nothing was changed).");
                foreach (DownloadCacheEntryInfo entry in targets)
                {
                    writer.WriteLine(
                        $"  [{MaintenanceCommandHelpers.ToCamelCase(entry.State)}] {FormatUrl(entry)}");
                }

                if (plannedFiles is { Count: > 0 })
                {
                    writer.WriteLine(
                        $"{plannedFiles.Count.ToString(CultureInfo.InvariantCulture)} file"
                        + (plannedFiles.Count == 1 ? "" : "s")
                        + (applied ? " removed:" : " would be removed:"));
                    foreach (string file in plannedFiles)
                    {
                        writer.WriteLine($"  file {file}");
                    }
                }

                if (unaddressable is { Count: > 0 })
                {
                    writer.WriteLine(
                        $"{unaddressable.Count.ToString(CultureInfo.InvariantCulture)} corrupt "
                        + "entr" + (unaddressable.Count == 1 ? "y has" : "ies have")
                        + " unreadable metadata and cannot be pruned by URL; run 'cache clear' to remove everything.");
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("directory", directory);
                writer.WriteString("action", action);
                writer.WriteBoolean("applied", applied);
                writer.WriteStartArray("entries");
                foreach (DownloadCacheEntryInfo entry in targets)
                {
                    WriteEntryJson(writer, entry);
                }

                writer.WriteEndArray();
                writer.WriteNumber("unaddressableCorruptEntries", unaddressable?.Count ?? 0);
                writer.WriteStartArray("files");
                foreach (string file in plannedFiles ?? [])
                {
                    writer.WriteStringValue(file);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });

    private static IReadOnlyList<string> InspectCacheFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ?
                [
                    .. Directory.EnumerateFiles(directory)
                        .Where(static path => !Path.GetFileName(path).Equals(
                            ".winmatsch-cache.lock",
                            StringComparison.Ordinal))
                        .Select(static path => Path.GetFileName(path)!)
                        .Order(StringComparer.Ordinal),
                ]
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new CliOperationException(
                $"Cache inventory failed: {exception.Message}",
                exception);
        }
    }

    private static void WriteEntryText(TextWriter writer, DownloadCacheEntryInfo entry, string indent)
    {
        writer.WriteLine($"{indent}Url: {FormatUrl(entry)}");
        writer.WriteLine($"{indent}State: {MaintenanceCommandHelpers.ToCamelCase(entry.State)}");
        writer.WriteLine($"{indent}Size: {FormatSize(entry)}");
        writer.WriteLine($"{indent}Sha256: {entry.ContentIdentity?.Sha256.Value ?? "-"}");
        writer.WriteLine($"{indent}Created: {FormatInstant(entry.CreatedAt)}");
        writer.WriteLine($"{indent}LastAccessed: {FormatInstant(entry.LastAccessedAt)}");
        writer.WriteLine($"{indent}Expires: {FormatInstant(entry.ExpiresAt)}");
    }

    private static void WriteEntryJson(Utf8JsonWriter writer, DownloadCacheEntryInfo entry)
    {
        writer.WriteStartObject();
        writer.WriteString("url", RedactUrl(entry.Url));
        writer.WriteString("cacheKey", entry.CacheKey);
        CliJson.WriteEnum(writer, "state", entry.State);
        if (entry.ContentIdentity is { } identity)
        {
            writer.WriteString("sha256", identity.Sha256.Value);
            writer.WriteNumber("sizeInBytes", identity.SizeInBytes);
        }
        else
        {
            writer.WriteNull("sha256");
            writer.WriteNull("sizeInBytes");
        }

        WriteInstant(writer, "createdAt", entry.CreatedAt);
        WriteInstant(writer, "lastAccessedAt", entry.LastAccessedAt);
        WriteInstant(writer, "expiresAt", entry.ExpiresAt);
        writer.WriteEndObject();
    }

    private static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            writer.WriteString(name, MaintenanceCommandHelpers.FormatTimestamp(instant));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string FormatInstant(DateTimeOffset? value)
        => value is { } instant ? MaintenanceCommandHelpers.FormatTimestamp(instant) : "-";

    private static string FormatSize(DownloadCacheEntryInfo entry)
        => entry.ContentIdentity is { } identity
            ? identity.SizeInBytes.ToString(CultureInfo.InvariantCulture) + " bytes"
            : "unknown size";

    /// <summary>
    /// The display form of an entry's URL: the entire userinfo component (username-only forms
    /// included) and all query values are redacted, since signed installer URLs routinely
    /// embed secrets. Commands still address entries by the exact original URL the caller
    /// already knows.
    /// </summary>
    private static string RedactUrl(string url)
        => CliRedactor.RedactUrl(url, redactAllQueryValues: true);

    private static string FormatUrl(DownloadCacheEntryInfo entry)
        => entry.Url.Length > 0 ? RedactUrl(entry.Url) : $"(unreadable metadata; key {entry.CacheKey})";
}
