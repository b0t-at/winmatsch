using System.Diagnostics;

namespace WinMatsch.GitHub.Auth;

/// <summary>
/// Stores the token in the freedesktop Secret Service via the <c>secret-tool</c> binary.
/// The secret travels only over stdin (store) and stdout (lookup) — never on the command
/// line, where other users could read it from the process table. Errors report exit codes
/// only, since secret-tool output could echo sensitive material.
/// </summary>
public sealed class LinuxSecretServiceTokenStore : ITokenStore
{
    private const string ExecutableName = "secret-tool";

    private readonly string _serviceName;
    private readonly string _accountName;
    private readonly Lazy<string?> _executablePath;

    /// <param name="serviceName">The Secret Service attribute value for <c>service</c>. Overridable for test isolation.</param>
    /// <param name="accountName">The Secret Service attribute value for <c>account</c>.</param>
    public LinuxSecretServiceTokenStore(
        string serviceName = TokenStores.ServiceName,
        string accountName = TokenStores.AccountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        _serviceName = serviceName;
        _accountName = accountName;
        _executablePath = new Lazy<string?>(static () => FindExecutable(ExecutableName, Environment.GetEnvironmentVariable("PATH")));
    }

    public string ProviderName => "Linux Secret Service (secret-tool)";

    public bool IsAvailable => OperatingSystem.IsLinux() && _executablePath.Value is not null;

    public async Task<GitHubToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        (int exitCode, string output) = await RunAsync(BuildLookupArguments(), null, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode == 1)
        {
            return null;
        }

        if (exitCode != 0)
        {
            throw new TokenStoreException($"secret-tool lookup failed with exit code {exitCode}.");
        }

        string value = output.TrimEnd('\r', '\n');
        return string.IsNullOrWhiteSpace(value) ? null : new GitHubToken(value);
    }

    public async Task SetTokenAsync(GitHubToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        (int exitCode, _) = await RunAsync(BuildStoreArguments(), token, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new TokenStoreException($"secret-tool store failed with exit code {exitCode}.");
        }
    }

    public async Task<bool> RemoveTokenAsync(CancellationToken cancellationToken = default)
    {
        (int exitCode, _) = await RunAsync(BuildClearArguments(), null, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return true;
        }

        if (exitCode == 1)
        {
            return false;
        }

        throw new TokenStoreException($"secret-tool clear failed with exit code {exitCode}.");
    }

    internal IReadOnlyList<string> BuildLookupArguments() =>
        ["lookup", "service", _serviceName, "account", _accountName];

    internal IReadOnlyList<string> BuildStoreArguments() =>
        ["store", "--label", $"{_serviceName} GitHub token", "service", _serviceName, "account", _accountName];

    internal IReadOnlyList<string> BuildClearArguments() =>
        ["clear", "service", _serviceName, "account", _accountName];

    /// <summary>Locates an executable on the given search path. Internal for cross-platform tests.</summary>
    internal static string? FindExecutable(string name, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> arguments,
        GitHubToken? secretForStdin,
        CancellationToken cancellationToken)
    {
        string executable = _executablePath.Value
            ?? throw new TokenStoreException("secret-tool was not found on PATH.");

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new TokenStoreException("secret-tool could not be started.");
        }

        try
        {
            Task<string> readStandardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task drainStandardError = process.StandardError.BaseStream.CopyToAsync(
                Stream.Null,
                cancellationToken);
            if (secretForStdin is not null)
            {
                // The secret is written without a trailing newline so it round-trips byte-exact.
                await process.StandardInput.WriteAsync(secretForStdin.RevealValue().AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await Task.WhenAll(
                readStandardOutput,
                drainStandardError,
                process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return (process.ExitCode, await readStandardOutput.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited between cancellation and the kill attempt.
            }

            throw;
        }
    }
}
