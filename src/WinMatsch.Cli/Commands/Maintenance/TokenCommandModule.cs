using System.CommandLine;
using WinMatsch.Cli.Hosting;
using WinMatsch.GitHub.Auth;

namespace WinMatsch.Cli.Commands.Maintenance;

/// <summary>
/// Manages the GitHub token stored in the OS keyring: <c>token add</c> validates and stores,
/// <c>token remove</c> removes idempotently, and <c>token status</c> reports redacted store
/// state. The secret is accepted only from standard input (recommended) or the global
/// <c>--token</c> option, stays wrapped in <see cref="GitHubToken"/> end to end, and is never
/// echoed, logged, serialized, or written to configuration.
/// </summary>
public sealed class TokenCommandModule : ICommandModule
{
    private readonly ITokenStore _store;
    private readonly ITokenValidator _validator;
    private readonly Func<TextReader> _standardInput;

    public TokenCommandModule(
        ITokenStore? store = null,
        ITokenValidator? validator = null,
        Func<TextReader>? standardInput = null)
    {
        _store = store ?? TokenStores.CreateDefault();
        _validator = validator ?? new GitHubTokenValidator();
        _standardInput = standardInput ?? (static () => Console.In);
    }

    public string Name => "token";

    public void RegisterCommands(ICommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var command = new Command("token", "Manage the GitHub token in the OS keyring.");
        command.Subcommands.Add(CreateAdd(registry));
        command.Subcommands.Add(CreateRemove(registry));
        command.Subcommands.Add(CreateStatus(registry));
        registry.AddCommand(command);
    }

    private Command CreateAdd(ICommandRegistry registry)
    {
        var stdin = new Option<bool>("--stdin")
        {
            Description = "Read the token from the first line of standard input (recommended; "
                + "keeps the secret out of the process argument list and shell history).",
        };
        var command = new Command(
            "add",
            "Validate a GitHub token and store it in the OS keyring. Supply it with --stdin "
            + "(recommended) or the global --token option.")
        {
            Options = { stdin },
        };

        registry.SetHandler(command, async context =>
        {
            GitHubToken token = ReadToken(context, context.ParseResult.GetValue(stdin), registry);
            EnsureStoreAvailable("stored");
            TokenValidationResult validation = await MaintenanceCommandHelpers.RunRemoteAsync(
                context,
                "Token validation failed",
                () => _validator.ValidateAsync(token, context.CancellationToken))
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new CliOperationException(
                    $"The token was not stored: {validation.FailureReason ?? "validation failed."}");
            }

            try
            {
                await _store.SetTokenAsync(token, context.CancellationToken).ConfigureAwait(false);
            }
            catch (TokenStoreException exception)
            {
                throw new CliOperationException(
                    $"The token could not be stored: {exception.Message} "
                    + "Use the GITHUB_TOKEN environment variable instead.",
                    exception);
            }

            WriteAddResult(context, validation);
            return ExitCodes.Success;
        });
        return command;
    }

    private Command CreateRemove(ICommandRegistry registry)
    {
        var command = new Command(
            "remove",
            "Remove the stored GitHub token from the OS keyring. Succeeds even when no token is stored.");
        registry.SetHandler(command, async context =>
        {
            EnsureStoreAvailable("removed");
            bool removed;
            try
            {
                removed = await _store.RemoveTokenAsync(context.CancellationToken).ConfigureAwait(false);
            }
            catch (TokenStoreException exception)
            {
                throw new CliOperationException(
                    $"The token could not be removed: {exception.Message}",
                    exception);
            }

            context.Output.WriteFormatted(
                writer => writer.WriteLine(removed
                    ? $"Removed the stored token from {_store.ProviderName}."
                    : $"No token was stored in {_store.ProviderName}; nothing to remove."),
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("provider", _store.ProviderName);
                    writer.WriteBoolean("removed", removed);
                    writer.WriteEndObject();
                });
            return ExitCodes.Success;
        });
        return command;
    }

    private Command CreateStatus(ICommandRegistry registry)
    {
        var command = new Command(
            "status",
            "Show whether a token is stored in the OS keyring. The token value is never shown.");
        registry.SetHandler(command, async context =>
        {
            TokenStoreStatus status = await TokenStores
                .GetStatusAsync(_store, context.CancellationToken)
                .ConfigureAwait(false);
            context.Output.WriteFormatted(
                writer =>
                {
                    writer.WriteLine($"Provider: {status.ProviderName}");
                    writer.WriteLine($"Available: {(status.IsAvailable ? "yes" : "no")}");
                    writer.WriteLine(status.HasToken switch
                    {
                        true => "Token: stored (value never shown)",
                        false => "Token: not stored",
                        null => "Token: unknown (keyring unavailable; use the GITHUB_TOKEN environment variable)",
                    });
                },
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("provider", status.ProviderName);
                    writer.WriteBoolean("available", status.IsAvailable);
                    if (status.HasToken is { } hasToken)
                    {
                        writer.WriteBoolean("hasToken", hasToken);
                    }
                    else
                    {
                        writer.WriteNull("hasToken");
                    }

                    writer.WriteEndObject();
                });
            return ExitCodes.Success;
        });
        return command;
    }

    private GitHubToken ReadToken(CommandContext context, bool fromStdin, ICommandRegistry registry)
    {
        GitHubToken? explicitToken = context.ParseResult.GetValue(registry.GlobalOptions.Token);
        if (explicitToken is not null)
        {
            if (fromStdin)
            {
                throw new CliUsageException("Pass either --stdin or --token, not both.");
            }

            return explicitToken;
        }

        if (!fromStdin)
        {
            throw new MissingInputException(
                "No token was provided. Pipe it via --stdin (recommended) or pass the global "
                + "--token option. Interactive prompting is not offered for secrets.");
        }

        string? line = _standardInput().ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new MissingInputException(
                "Standard input did not provide a token on its first line.");
        }

        try
        {
            return new GitHubToken(line.Trim());
        }
        catch (ArgumentException exception)
        {
            // Never echo the raw value; it is a (malformed) secret.
            throw new CliUsageException(
                "The token read from standard input is invalid: it must be non-empty and "
                + "contain no whitespace or control characters.",
                exception);
        }
    }

    private void EnsureStoreAvailable(string action)
    {
        if (!_store.IsAvailable)
        {
            throw new CliOperationException(
                $"The token cannot be {action}: no OS keyring is available on this machine "
                + $"(provider: {_store.ProviderName}). Use the GITHUB_TOKEN environment variable "
                + "or the --token option instead.");
        }
    }

    private void WriteAddResult(CommandContext context, TokenValidationResult validation)
        => context.Output.WriteFormatted(
            writer =>
            {
                writer.WriteLine($"Stored a valid token for {validation.Login} in {_store.ProviderName}.");
                if (validation.Scopes.Count > 0)
                {
                    writer.WriteLine($"Scopes: {string.Join(", ", validation.Scopes)}");
                }
            },
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("provider", _store.ProviderName);
                writer.WriteBoolean("stored", true);
                writer.WriteString("login", validation.Login);
                writer.WriteStartArray("scopes");
                foreach (string scope in validation.Scopes)
                {
                    writer.WriteStringValue(scope);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
}
