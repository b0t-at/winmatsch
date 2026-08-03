using WinMatsch.Testing.Infrastructure;
using Xunit;

namespace WinMatsch.Testing.Tests;

public sealed class TestDoubleTests
{
    [Fact]
    public async Task Fake_clock_advances_deterministically()
    {
        var initial = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(initial);

        await clock.DelayAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(initial.AddMinutes(2).AddSeconds(30), clock.UtcNow);
        Assert.Equal([TimeSpan.FromSeconds(30)], clock.Delays);
    }

    [Fact]
    public void In_memory_file_system_commits_streams_and_moves_atomically()
    {
        var fileSystem = new InMemoryFileSystem();
        string source = Path.Combine("tmp", "source.bin");
        string destination = Path.Combine("cache", "destination.bin");

        using (Stream stream = fileSystem.CreateFile(source))
        {
            stream.Write("fixture"u8);
        }

        fileSystem.MoveFile(source, destination, overwrite: false);

        Assert.False(fileSystem.FileExists(source));
        Assert.Equal("fixture"u8.ToArray(), fileSystem.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Fake_process_runner_records_requests_and_returns_queued_result()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(7, "stdout", "stderr"));
        var request = new ProcessRequest
        {
            FileName = "tool.exe",
            Arguments = ["--version"],
        };

        ProcessResult result = await runner.RunAsync(request);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("stdout", result.StandardOutput);
        Assert.Same(request, Assert.Single(runner.Requests));
    }
}
