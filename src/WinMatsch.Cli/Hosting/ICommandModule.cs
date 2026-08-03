using System.CommandLine;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// The contract a feature area implements to plug its commands into the host. Modules are
/// self-contained: each registers its own commands through the <see cref="ICommandRegistry"/>
/// it receives, so later modules can be added without modifying the host or one another.
/// Modules must not add global options or mutate commands they did not create.
/// </summary>
public interface ICommandModule
{
    /// <summary>A short unique module name, used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Registers this module's commands. Called once while the host is composed.</summary>
    public void RegisterCommands(ICommandRegistry registry);
}

/// <summary>
/// The registration surface handed to <see cref="ICommandModule"/>. Handlers bound through
/// <see cref="SetHandler"/> receive a fully composed <see cref="CommandContext"/>; the host
/// owns configuration resolution, interaction/output construction, cancellation wiring, and
/// the mapping of exceptions to the <see cref="ExitCodes"/> contract.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>The shared global options, for reading additional values from the parse result.</summary>
    public GlobalOptions GlobalOptions { get; }

    /// <summary>Adds a top-level command to the root command.</summary>
    public void AddCommand(Command command);

    /// <summary>
    /// Binds the handler for a command (top-level or nested). The handler's return value is the
    /// process exit code; thrown <see cref="CliUsageException"/>, <see cref="MissingInputException"/>,
    /// <see cref="CliOperationException"/>, and <see cref="OperationCanceledException"/> are
    /// translated to their documented exit codes, anything else to
    /// <see cref="ExitCodes.UnexpectedError"/>.
    /// </summary>
    public void SetHandler(Command command, Func<CommandContext, Task<int>> handler);
}
