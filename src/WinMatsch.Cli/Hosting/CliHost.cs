using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using WinMatsch.Cli.Interaction;
using WinMatsch.Cli.Output;
using WinMatsch.GitHub.Auth;
using WinMatsch.Workflows;
using WinMatsch.Workflows.Configuration;

namespace WinMatsch.Cli.Hosting;

/// <summary>
/// The composition root of the winmatsch CLI. Owns command-line parsing (System.CommandLine),
/// module registration, configuration binding
/// (<c>command &gt; environment &gt; user file &gt; defaults</c>), interaction and output
/// construction, cancellation wiring, and the deterministic mapping of every outcome to the
/// <see cref="ExitCodes"/> contract.
/// </summary>
public sealed class CliHost
{
    private readonly CliHostOptions _options;
    private readonly GlobalOptions _globalOptions;
    private readonly RootCommand _rootCommand;

    public CliHost(CliHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _globalOptions = new GlobalOptions();
        _rootCommand = BuildRootCommand();
    }

    /// <summary>Creates the production host over the real console and OS keyring.</summary>
    public static CliHost CreateDefault(IReadOnlyList<ICommandModule>? modules = null) =>
        new(new CliHostOptions
        {
            Output = Console.Out,
            Error = Console.Error,
            IsInputRedirected = Console.IsInputRedirected,
            IsOutputRedirected = Console.IsOutputRedirected,
            IsErrorRedirected = Console.IsErrorRedirected,
            Modules = modules ?? [],
        });

    /// <summary>
    /// Parses and executes one invocation. Never throws for user-facing failures: parse errors
    /// return <see cref="ExitCodes.UsageError"/> with messages on standard error, and
    /// cancellation returns <see cref="ExitCodes.Cancelled"/>.
    /// </summary>
    public async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        ParseResult parseResult = _rootCommand.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (ParseError parseError in parseResult.Errors)
            {
                _options.Error.WriteLine(parseError.Message);
            }

            _options.Error.WriteLine("Run with '--help' for usage.");
            return ExitCodes.UsageError;
        }

        var invocation = new InvocationConfiguration
        {
            Output = _options.Output,
            Error = _options.Error,
            EnableDefaultExceptionHandler = false,
        };

        try
        {
            return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _options.Error.WriteLine("The operation was cancelled.");
            return ExitCodes.Cancelled;
        }
    }

    private RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "winmatsch: cross-platform WinGet manifest automation.");

        foreach (Option option in _globalOptions.All)
        {
            root.Options.Add(option);
        }

        foreach (VersionOption versionOption in root.Options.OfType<VersionOption>())
        {
            versionOption.Action = new WriteCliVersionAction();
        }

        // A bare `winmatsch` prints help; picking a command is the user's next step.
        root.SetAction(parseResult => new HelpAction().Invoke(parseResult));

        var registry = new CommandRegistry(this, root);
        foreach (ICommandModule module in _options.Modules)
        {
            module.RegisterCommands(registry);
        }

        return root;
    }

    private async Task<int> ExecuteHandlerAsync(
        ParseResult parseResult,
        Func<CommandContext, Task<int>> handler,
        CancellationToken cancellationToken)
    {
        TextWriter error = _options.Error;

        CommandContext context;
        try
        {
            context = CreateContext(parseResult, cancellationToken);
        }
        catch (Exception exception)
            when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            // FileNotFoundException (missing --config) is an IOException; unreadable
            // configuration files (locked, deny-read ACL) land here too.
            error.WriteLine($"Configuration error: {exception.Message}");
            return ExitCodes.ConfigurationError;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("The operation was cancelled.");
            return ExitCodes.Cancelled;
        }
#pragma warning disable CA1031 // Nothing may escape the host; unclassified failures map to exit code 1.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            error.WriteLine($"Unexpected error: {exception.Message}");
            return ExitCodes.UnexpectedError;
        }

        try
        {
            return await handler(context).ConfigureAwait(false);
        }
        catch (CliUsageException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCodes.UsageError;
        }
        catch (MissingInputException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCodes.MissingInput;
        }
        catch (CliOperationException exception)
        {
            error.WriteLine(exception.Message);
            return ExitCodes.OperationFailed;
        }
        catch (FormatException exception)
        {
            // By repository convention FormatException always carries a bad user-supplied
            // value (configuration, environment, or option content) discovered lazily, such
            // as a malformed GITHUB_TOKEN resolved at handler time.
            error.WriteLine($"Configuration error: {exception.Message}");
            return ExitCodes.ConfigurationError;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("The operation was cancelled.");
            return ExitCodes.Cancelled;
        }
#pragma warning disable CA1031 // The host is the last-chance boundary mapping bugs to exit code 1.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            error.WriteLine($"Unexpected error: {exception.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    private CommandContext CreateContext(ParseResult parseResult, CancellationToken cancellationToken)
    {
        ConfigurationLayer commandLayer = _globalOptions.BindConfigurationLayer(parseResult);
        ConfigurationLayer environmentLayer = EnvironmentConfiguration.Read(_options.EnvironmentVariables);
        ConfigurationLayer userLayer = UserConfigurationFile.Load(
            parseResult.GetValue(_globalOptions.ConfigFile),
            _options.EnvironmentVariables,
            _options.ReadTextFile,
            _options.HomeDirectory);

        WinMatschConfiguration configuration = ConfigurationResolver.Resolve(
            commandLayer, environmentLayer, userLayer);

        ConsoleCapabilities capabilities = ConsoleCapabilities.Resolve(
            configuration.Interaction,
            configuration.OutputFormat,
            parseResult.GetValue(_globalOptions.NoColor),
            _options.EnvironmentVariables,
            _options.IsInputRedirected,
            _options.IsErrorRedirected);

        var tokenResolver = new TokenResolver(
            _options.TokenStore ?? TokenStores.CreateDefault(),
            _options.EnvironmentVariables);

        return new CommandContext
        {
            ParseResult = parseResult,
            Configuration = configuration,
            ExecutionMode = parseResult.GetValue(_globalOptions.DryRun)
                ? WorkflowExecutionMode.Plan
                : WorkflowExecutionMode.Apply,
            Capabilities = capabilities,
            Interaction = CreateInteraction(capabilities),
            Output = new CommandOutput(_options.Output, _options.Error, configuration.OutputFormat),
            Tokens = new TokenAccessor(tokenResolver, parseResult.GetValue(_globalOptions.Token)),
            CancellationToken = cancellationToken,
        };
    }

    private IUserInteraction CreateInteraction(ConsoleCapabilities capabilities)
    {
        if (_options.InteractionFactory is not null)
        {
            return _options.InteractionFactory(new InteractionCreation(capabilities, _options.Error));
        }

        if (capabilities.PromptsEnabled)
        {
            return new SpectreUserInteraction(
                SpectreUserInteraction.CreateErrorConsole(_options.Error, capabilities.ColorEnabled));
        }

        return new NonInteractiveUserInteraction(
            _options.Error,
            capabilities.PromptsDisabledReason ?? "non-interactive session");
    }

    private sealed class WriteCliVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(CliVersion.InformationalVersion);
            return ExitCodes.Success;
        }
    }

    private sealed class CommandRegistry : ICommandRegistry
    {
        private readonly CliHost _host;
        private readonly RootCommand _root;

        public CommandRegistry(CliHost host, RootCommand root)
        {
            _host = host;
            _root = root;
        }

        public GlobalOptions GlobalOptions => _host._globalOptions;

        public void AddCommand(Command command)
        {
            ArgumentNullException.ThrowIfNull(command);
            _root.Subcommands.Add(command);
        }

        public void SetHandler(Command command, Func<CommandContext, Task<int>> handler)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(handler);
            command.SetAction((parseResult, cancellationToken) =>
                _host.ExecuteHandlerAsync(parseResult, handler, cancellationToken));
        }
    }
}
