using System.CommandLine;
using WinMatsch.Cli.Hosting;

namespace WinMatsch.Cli.Tests.Harness;

/// <summary>
/// A minimal command module registering a single <c>probe</c> command whose handler is
/// supplied by the test. The last composed <see cref="CommandContext"/> is captured so tests
/// can assert on configuration binding, capabilities, and execution mode.
/// </summary>
public sealed class ProbeModule : ICommandModule
{
    private readonly Func<CommandContext, Task<int>> _handler;

    public ProbeModule(Func<CommandContext, Task<int>>? handler = null)
    {
        _handler = handler ?? (_ => Task.FromResult(ExitCodes.Success));
    }

    public string Name => "probe";

    public CommandContext? LastContext { get; private set; }

    public void RegisterCommands(ICommandRegistry registry)
    {
        var command = new Command("probe", "Test probe command.");
        registry.AddCommand(command);
        registry.SetHandler(command, context =>
        {
            LastContext = context;
            return _handler(context);
        });
    }
}
