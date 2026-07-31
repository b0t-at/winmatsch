using System.Diagnostics;

namespace WinMatsch.Testing.Infrastructure;

public sealed record ProcessRequest
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PhysicalProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in request.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process '{request.FileName}'.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}

public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<ProcessRequest, ProcessResult>> _results = [];

    public List<ProcessRequest> Requests { get; } = [];

    public void Enqueue(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results.Enqueue(_ => result);
    }

    public void Enqueue(Func<ProcessRequest, ProcessResult> resultFactory)
    {
        ArgumentNullException.ThrowIfNull(resultFactory);
        _results.Enqueue(resultFactory);
    }

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                $"No fake process result was queued for '{request.FileName}'.");
        }

        return Task.FromResult(_results.Dequeue()(request));
    }
}
