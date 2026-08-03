namespace WinMatsch.Cli;

/// <summary>
/// Thrown by command handlers when the invocation is well-formed syntactically but used
/// incorrectly (for example mutually exclusive options). Maps to
/// <see cref="ExitCodes.UsageError"/>; the message is written to standard error.
/// </summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }

    public CliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when required input is missing and prompting is unavailable. Maps to
/// <see cref="ExitCodes.MissingInput"/>. The message must tell the user which
/// non-interactive channel (option, environment variable, configuration key) supplies
/// the value.
/// </summary>
public sealed class MissingInputException : Exception
{
    public MissingInputException(string message)
        : base(message)
    {
    }

    public MissingInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown by command handlers when their operation fails for a domain reason after a valid
/// invocation (validation failure, missing remote resource, rejected submission). Maps to
/// <see cref="ExitCodes.OperationFailed"/>.
/// </summary>
public sealed class CliOperationException : Exception
{
    public CliOperationException(string message)
        : base(message)
    {
    }

    public CliOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
