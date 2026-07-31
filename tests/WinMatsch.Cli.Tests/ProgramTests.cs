using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void Version_command_succeeds()
    {
        int exitCode = Program.Main(["--version"]);

        Assert.Equal(0, exitCode);
    }
}
