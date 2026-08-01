namespace WinMatsch.Cli;

/// <summary>
/// The deterministic exit-code contract of the <c>winmatsch</c> executable. Every invocation
/// terminates with exactly one of these values, so scripts and CI pipelines can branch on the
/// failure category without parsing output.
/// </summary>
public static class ExitCodes
{
    /// <summary>The command completed successfully. In plan mode this means the plan is valid.</summary>
    public const int Success = 0;

    /// <summary>
    /// An unexpected internal error (a bug, or an unclassified failure). The message is written
    /// to standard error; standard output stays untouched.
    /// </summary>
    public const int UnexpectedError = 1;

    /// <summary>
    /// The command line could not be parsed or was used incorrectly: unknown commands or
    /// options, malformed option values, or a <see cref="CliUsageException"/> from a command.
    /// </summary>
    public const int UsageError = 2;

    /// <summary>
    /// The effective configuration is invalid: a malformed <c>WINMATSCH_*</c> environment
    /// variable, an unreadable or invalid configuration file, or contradictory settings.
    /// </summary>
    public const int ConfigurationError = 3;

    /// <summary>
    /// Required input was not provided and prompting is unavailable (non-interactive session,
    /// <c>--interaction never</c>, or JSON output). Supply the missing value via an option,
    /// environment variable, or configuration file.
    /// </summary>
    public const int MissingInput = 4;

    /// <summary>
    /// The command executed but its operation failed for a domain reason (validation failed,
    /// resource not found, remote rejection), or the user declined/cancelled an interactive
    /// confirmation without sending SIGINT. Reserved for command modules via
    /// <see cref="CliOperationException"/>.
    /// </summary>
    public const int OperationFailed = 5;

    /// <summary>
    /// The invocation received Ctrl+C/SIGINT or its propagated cancellation token was cancelled.
    /// Interactive "no" answers use <see cref="OperationFailed"/>, never this POSIX
    /// 128+SIGINT code.
    /// </summary>
    public const int Cancelled = 130;
}
