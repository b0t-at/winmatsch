using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Downloads;
using WinMatsch.GitHub;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows.GitHub;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Shared behavior of the maintenance command modules: destructive-action confirmation,
/// lifecycle result-code to exit-code mapping, operational-failure classification, and
/// deterministic diagnostic rendering. All helpers honor the host contracts — confirmation
/// never defaults to yes, JSON mode never prompts, and secrets stay redacted.
/// </summary>
internal static class MaintenanceCommandHelpers
{
    /// <summary>
    /// Confirms a mutating action. Returns true only when <paramref name="assumeYes"/> was
    /// passed or the user explicitly answered yes. When prompting is unavailable and
    /// <paramref name="assumeYes"/> was not passed, throws <see cref="MissingInputException"/>
    /// (exit code 4) instead of assuming consent.
    /// </summary>
    public static async Task<bool> ConfirmMutationAsync(
        CommandContext context,
        bool assumeYes,
        string question)
    {
        if (assumeYes)
        {
            return true;
        }

        if (!context.Interaction.CanPrompt)
        {
            throw new MissingInputException(
                "Confirmation is required for this action but prompting is unavailable "
                + $"({context.Capabilities.PromptsDisabledReason ?? "non-interactive session"}). "
                + "Pass --yes to confirm explicitly.");
        }

        return await context.Interaction
            .ConfirmAsync(question, defaultValue: false, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps lifecycle outcomes to the documented contract. A result-shaped cancellation without
    /// a cancelled invocation token is an operation failure; only propagated Ctrl+C reaches 130.
    /// </summary>
    public static int MapResultCode(
        GitHubLifecycleResultCode code,
        CancellationToken cancellationToken) => code switch
        {
            GitHubLifecycleResultCode.Succeeded
                or GitHubLifecycleResultCode.Planned
                or GitHubLifecycleResultCode.NoAction => ExitCodes.Success,
            GitHubLifecycleResultCode.Cancelled when cancellationToken.IsCancellationRequested
                => ExitCodes.Cancelled,
            _ => ExitCodes.OperationFailed,
        };

    /// <summary>
    /// The exception types a maintenance handler converts to <see cref="CliOperationException"/>.
    /// Remote or payload <see cref="FormatException"/>s are operation errors here — by the time
    /// a handler runs, configuration is already resolved, so a lazily surfaced format failure
    /// carries remote data, not user configuration.
    /// </summary>
    public static bool IsOperationalFailure(Exception exception)
        => exception is FormatException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or HttpRequestException
            or JsonException
            or DownloadException
            or TokenStoreException
            or YamlDotNet.Core.YamlException;

    public static bool IsKeyringFailure(Exception exception)
        => exception is TokenStoreException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception;

    /// <summary>Runs a remote operation converting operational failures and preserving cancellation.</summary>
    public static async Task<T> RunRemoteAsync<T>(
        CommandContext context,
        string failurePrefix,
        Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new CliOperationException(
                $"{failurePrefix}: the remote request timed out. {Redact(exception.Message)}",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            throw new CliOperationException(
                $"{failurePrefix}: {Redact(exception.Message)}",
                exception);
        }
    }

    /// <summary>Redacts secret-shaped content from a display message.</summary>
    public static string Redact(string message) => CliRedactor.Redact(message);

    /// <summary>Parses an <c>owner/name</c> option value into repository coordinates.</summary>
    public static RepositoryCoordinates ParseRepository(string value, string optionName)
    {
        try
        {
            return RepositoryCoordinates.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new CliUsageException($"{optionName}: {exception.Message}", exception);
        }
    }

    /// <summary>Formats a timestamp deterministically (ISO 8601, UTC, invariant).</summary>
    public static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Lower-camel-cases an enum value for stable text and JSON output.</summary>
    public static string ToCamelCase<T>(T value)
        where T : struct, Enum
        => CliJson.EnumValue(value);

    /// <summary>Writes plan operations and diagnostics as stable text lines.</summary>
    public static void WritePlanText(
        TextWriter writer,
        GitHubMaintenancePlan plan,
        IReadOnlyList<GitHubLifecycleDiagnostic> diagnostics)
    {
        writer.WriteLine("Planned operations:");
        if (plan.Operations.IsEmpty)
        {
            writer.WriteLine("  (none)");
        }

        foreach (PlannedRemoteOperation operation in plan.Operations)
        {
            writer.WriteLine(
                $"  {ToCamelCase(operation.Kind)} {Redact(operation.Target)}: "
                + Redact(operation.Description));
        }

        WriteDiagnosticsText(writer, diagnostics);
    }

    /// <summary>Writes diagnostics as stable text lines.</summary>
    public static void WriteDiagnosticsText(
        TextWriter writer,
        IReadOnlyList<GitHubLifecycleDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        writer.WriteLine("Diagnostics:");
        foreach (GitHubLifecycleDiagnostic diagnostic in diagnostics)
        {
            writer.WriteLine($"  {diagnostic.Code}: {Redact(diagnostic.Message)}");
        }
    }

    /// <summary>Writes plan operations and diagnostics into an open JSON object.</summary>
    public static void WritePlanJson(
        Utf8JsonWriter writer,
        GitHubMaintenancePlan plan,
        IReadOnlyList<GitHubLifecycleDiagnostic> diagnostics)
    {
        writer.WriteStartArray("operations");
        foreach (PlannedRemoteOperation operation in plan.Operations)
        {
            writer.WriteStartObject();
            CliJson.WriteEnum(writer, "kind", operation.Kind);
            writer.WriteString("target", Redact(operation.Target));
            writer.WriteString("description", Redact(operation.Description));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteDiagnosticsJson(writer, diagnostics);
    }

    /// <summary>Writes a <c>diagnostics</c> array into an open JSON object.</summary>
    public static void WriteDiagnosticsJson(
        Utf8JsonWriter writer,
        IReadOnlyList<GitHubLifecycleDiagnostic> diagnostics)
    {
        writer.WriteStartArray("diagnostics");
        foreach (GitHubLifecycleDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("message", Redact(diagnostic.Message));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
