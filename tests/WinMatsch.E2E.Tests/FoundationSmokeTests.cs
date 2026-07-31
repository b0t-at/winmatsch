using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class FoundationSmokeTests
{
    [Fact]
    public void Production_assemblies_load()
    {
        Assert.NotNull(typeof(Cli.Program).Assembly);
        Assert.NotNull(typeof(Workflows.OperationPlan).Assembly);
    }
}
