using System.Text.Json;
using System.Text.Json.Serialization;
using WinMatsch.Core;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Output;

internal sealed record ResultJsonOutcome(
    string? Command,
    bool Success,
    int ExitCode,
    string? PackageIdentifier,
    string? PackageVersion,
    string? ManifestPath,
    ResultJsonPullRequest? PullRequest,
    ResultJsonError? Error);

internal sealed record ResultJsonPullRequest(string Url, long Number);

internal sealed record ResultJsonError(string Code, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResultJsonOutcome))]
internal sealed partial class ResultJsonContext : JsonSerializerContext;

internal sealed class ResultJsonRecorder
{
    private WorkflowOperationResult? _local;
    private GitHubLifecycleResult? _remote;
    private ResultJsonError? _hostError;

    public void CaptureMutation(
        WorkflowOperationResult local,
        GitHubLifecycleResult? remote)
    {
        _local = local;
        _remote = remote;
    }

    public void CaptureHostError(string code, string message)
    {
        _hostError = new(code, Redact(message));
    }

    public ResultJsonOutcome Create(string? command, int exitCode)
    {
        bool success = exitCode == ExitCodes.Success;
        LocalOperationPlan? plan = _local?.Plan;
        return new(
            command,
            success,
            exitCode,
            plan?.PackageIdentifier.Value,
            plan?.PackageVersion.Value,
            plan is null ? null : ManifestPath(plan),
            PullRequest(),
            success ? null : Error(exitCode));
    }

    private ResultJsonPullRequest? PullRequest()
    {
        RemoteMutationState? state = _remote?.RemoteState;
        return state is
        {
            PullRequestCreated: true,
            PullRequestUri: { } uri,
            PullRequestNumber: long number,
        }
            ? new(Redact(uri.AbsoluteUri), number)
            : null;
    }

    private ResultJsonError Error(int exitCode)
    {
        (string? code, string? message) = DomainError();
        if (_hostError is not null)
        {
            return code is not null && message is not null
                ? new(code, Redact(message))
                : _hostError;
        }

        return new(
            code ?? FallbackCode(exitCode),
            Redact(message ?? FallbackMessage(exitCode)));
    }

    private (string? Code, string? Message) DomainError()
    {
        if (_remote is not null
            && _remote.Code is not (
                GitHubLifecycleResultCode.Succeeded
                or GitHubLifecycleResultCode.Planned))
        {
            GitHubLifecycleDiagnostic? diagnostic = _remote.Diagnostics.FirstOrDefault();
            return diagnostic is not null
                ? (diagnostic.Code, diagnostic.Message)
                : (RemoteCode(_remote.Code), $"Remote submission ended with {_remote.Code}.");
        }

        if (_local is null)
        {
            return (null, null);
        }

        if (_local.Code == WorkflowResultCode.Succeeded)
        {
            return string.IsNullOrWhiteSpace(_local.ErrorMessage)
                ? (null, null)
                : ("WF_APPLY_FAILED", _local.ErrorMessage);
        }

        WorkflowQuestion? question = _local.Plan.Questions.FirstOrDefault();
        if (question is not null)
        {
            return (question.Code, question.Prompt);
        }

        ValidationFinding? finding = _local.Plan.Validation.Findings.FirstOrDefault(
            static value => value.Severity != ValidationSeverity.Info);
        if (finding is not null)
        {
            return (finding.Code, finding.Message);
        }

        return (
            LocalCode(_local.Code),
            _local.ErrorMessage ?? $"Local mutation ended with {_local.Code}.");
    }

    private static string ManifestPath(LocalOperationPlan plan)
        => Path.GetFullPath(Path.Combine(
            plan.OutputDirectory,
            ManifestPaths.GetVersionDirectory(
                    plan.PackageIdentifier,
                    plan.PackageVersion)
                .Replace('/', Path.DirectorySeparatorChar)));

    private static string LocalCode(WorkflowResultCode code) => code switch
    {
        WorkflowResultCode.QuestionsRequired => "QUESTIONS_REQUIRED",
        WorkflowResultCode.ReviewRequired => "WF_REVIEW_REQUIRED",
        WorkflowResultCode.ValidationFailed => "VALIDATION_FAILED",
        WorkflowResultCode.NoChanges => "WF_NO_CHANGES",
        WorkflowResultCode.NotFound => "WF_NOT_FOUND",
        WorkflowResultCode.Conflict => "WF_CONFLICT",
        WorkflowResultCode.InvalidRequest => "WF_INVALID",
        WorkflowResultCode.ApplyFailed => "WF_APPLY_FAILED",
        WorkflowResultCode.StalePlan => "WF_STALE_PLAN",
        _ => "OPERATION_FAILED",
    };

    private static string RemoteCode(GitHubLifecycleResultCode code) => code switch
    {
        GitHubLifecycleResultCode.InvalidPlan => "GH_INVALID_PLAN",
        GitHubLifecycleResultCode.ConsentRequired => "GH_CONSENT_REQUIRED",
        GitHubLifecycleResultCode.DuplicatePullRequest => "GH_DUPLICATE_PULL_REQUEST",
        GitHubLifecycleResultCode.DuplicateInstallerHash => "GH_DUPLICATE_INSTALLER_HASH",
        GitHubLifecycleResultCode.Conflict => "GH_CONFLICT",
        GitHubLifecycleResultCode.Cancelled => "GH_CANCELLED",
        GitHubLifecycleResultCode.ValidationFailed => "GH_VALIDATION_FAILED",
        GitHubLifecycleResultCode.RemoteFailure => "GH_REMOTE_FAILURE",
        GitHubLifecycleResultCode.HumanEscalationRequired => "GH_HUMAN_ESCALATION_REQUIRED",
        GitHubLifecycleResultCode.NoAction => "GH_NO_ACTION",
        _ => "OPERATION_FAILED",
    };

    private static string FallbackCode(int exitCode) => exitCode switch
    {
        ExitCodes.UnexpectedError => "UNEXPECTED_ERROR",
        ExitCodes.UsageError => "USAGE_ERROR",
        ExitCodes.ConfigurationError => "CONFIGURATION_ERROR",
        ExitCodes.MissingInput => "MISSING_INPUT",
        ExitCodes.OperationFailed => "OPERATION_FAILED",
        ExitCodes.Cancelled => "CANCELLED",
        _ => "OPERATION_FAILED",
    };

    private static string FallbackMessage(int exitCode) => exitCode switch
    {
        ExitCodes.UnexpectedError => "The command failed because of an unexpected internal error.",
        ExitCodes.UsageError => "The command line could not be parsed or was used incorrectly.",
        ExitCodes.ConfigurationError => "The effective configuration is invalid.",
        ExitCodes.MissingInput => "Required input was not provided and prompting is unavailable.",
        ExitCodes.Cancelled => "The operation was cancelled.",
        _ => "The command failed.",
    };

    private static string Redact(string value)
        => CliRedactor.RedactUrl(value, redactAllQueryValues: true);
}

internal static class ResultJsonWriter
{
    public static void Write(string path, ResultJsonOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(outcome);

        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    outcome,
                    ResultJsonContext.Default.ResultJsonOutcome);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The primary write/move failure is more useful than best-effort cleanup failure.
            }
        }
    }
}
