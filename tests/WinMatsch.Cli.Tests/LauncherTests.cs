using WinMatsch.Cli.Commands.Mutations;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class LauncherTests
{
    [Theory]
    [InlineData(UrlLauncherPlatform.Windows, "explorer.exe")]
    [InlineData(UrlLauncherPlatform.MacOS, "open")]
    [InlineData(UrlLauncherPlatform.Linux, "xdg-open")]
    public async Task Launcher_uses_platform_command_with_one_literal_argument(
        UrlLauncherPlatform platform,
        string executable)
    {
        var processes = new RecordingUrlProcessRunner();
        var launcher = new ProcessUrlLauncher(() => platform, processes);
        var uri = new Uri("https://example.test/pull/42?value=%26calc");

        await launcher.OpenAsync(uri);

        Assert.Equal(executable, processes.Executable);
        Assert.Equal([uri.AbsoluteUri], processes.Arguments);
    }

    [Fact]
    public async Task Launcher_maps_nonzero_process_exit_to_an_error()
    {
        var launcher = new ProcessUrlLauncher(
            () => UrlLauncherPlatform.Linux,
            new RecordingUrlProcessRunner { ExitCode = 3 });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.OpenAsync(new Uri("https://example.test/pull/42")));

        Assert.Contains("code 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launcher_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var launcher = new ProcessUrlLauncher(
            () => UrlLauncherPlatform.Linux,
            new RecordingUrlProcessRunner());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => launcher.OpenAsync(
                new Uri("https://example.test/pull/42"),
                cancellation.Token));
    }

    private sealed class RecordingUrlProcessRunner : IUrlProcessRunner
    {
        public int ExitCode { get; init; }

        public string? Executable { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<int> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executable = executable;
            Arguments = arguments;
            return Task.FromResult(ExitCode);
        }
    }
}
