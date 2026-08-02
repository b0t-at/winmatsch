using System.Diagnostics;
using Xunit;

namespace WinMatsch.E2E.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string purpose = "run")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "winmatsch-e2e",
            purpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        const int maximumAttempts = 5;
        for (int attempt = 1; attempt < maximumAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliProcess
{
    public static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        string assembly = System.IO.Path.Combine(AppContext.BaseDirectory, "winmatsch.dll");
        if (!File.Exists(assembly))
        {
            throw new InvalidOperationException($"CLI assembly not found at '{assembly}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = FindDotnet(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["NO_COLOR"] = null;
        startInfo.Environment["CI"] = null;
        startInfo.Environment["GITHUB_ACTIONS"] = null;
        startInfo.Environment["TF_BUILD"] = null;
        startInfo.Environment["GITHUB_TOKEN"] = null;
        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The winmatsch process could not be started.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new(process.ExitCode, await stdout, await stderr);
    }

    public static void AssertSafe(ProcessResult result, params string[] secrets)
    {
        string output = result.StandardOutput + result.StandardError;
        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
        Assert.All(
            secrets,
            secret => Assert.DoesNotContain(secret, output, StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindDotnet()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && System.IO.Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string candidate = System.IO.Path.Combine(
                root,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }
}

internal sealed class StaticHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request.Method, request.RequestUri!));
        HttpResponseMessage response = _responseFactory(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

internal sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable, string? requiredValue = null)
    {
        string? actual = Environment.GetEnvironmentVariable(variable);
        if (requiredValue is null ? string.IsNullOrWhiteSpace(actual) : actual != requiredValue)
        {
            Skip = requiredValue is null
                ? $"Set {variable} to enable this opt-in contract."
                : $"Set {variable}={requiredValue} to enable this opt-in contract.";
        }
    }
}

internal sealed class WindowsEnvironmentFactAttribute : FactAttribute
{
    public WindowsEnvironmentFactAttribute(string variable, string requiredValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This opt-in compiler corpus requires Windows.";
            return;
        }

        if (Environment.GetEnvironmentVariable(variable) != requiredValue)
        {
            Skip = $"Set {variable}={requiredValue} to enable this opt-in contract.";
        }
    }
}
