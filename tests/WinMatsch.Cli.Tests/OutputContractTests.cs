using System.Text.Json;
using WinMatsch.Cli.Tests.Harness;
using Xunit;

namespace WinMatsch.Cli.Tests;

public sealed class OutputContractTests
{
    [Fact]
    public async Task Results_go_to_stdout_and_diagnostics_to_stderr()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteDiagnostic("checking things");
            context.Output.WriteResult("the result");
            context.Interaction.ReportStatus("still working");
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("the result\n", result.StandardOutput);
        Assert.Contains("checking things", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("checking things", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_results_are_stable_compact_and_newline_terminated()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteJsonResult(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("name", "Fancy.Package");
                writer.WriteNumber("installers", 2);
                writer.WriteBoolean("dryRun", context.IsDryRun);
                writer.WriteEndObject();
            });
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("{\"name\":\"Fancy.Package\",\"installers\":2,\"dryRun\":true}\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        // The document parses back cleanly.
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Fancy.Package", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WriteFormatted_picks_text_in_text_mode()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteFormatted(
                writer => writer.WriteLine("plain text"),
                json =>
                {
                    json.WriteStartObject();
                    json.WriteEndObject();
                });
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe"]);

        Assert.Equal("plain text\n", result.StandardOutput);
    }

    [Fact]
    public async Task WriteFormatted_picks_json_in_json_mode()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(context =>
        {
            context.Output.WriteFormatted(
                writer => writer.WriteLine("plain text"),
                json =>
                {
                    json.WriteStartObject();
                    json.WriteString("kind", "result");
                    json.WriteEndObject();
                });
            return Task.FromResult(ExitCodes.Success);
        }));

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json"]);

        Assert.Equal("{\"kind\":\"result\"}\n", result.StandardOutput);
    }

    [Fact]
    public async Task Errors_never_contaminate_stdout_in_json_mode()
    {
        var harness = new CliHarness();
        harness.Modules.Add(new ProbeModule(_ =>
            throw new CliOperationException("validation failed")));

        CliRunResult result = await harness.RunAsync(["probe", "--format", "json"]);

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("validation failed", result.StandardError, StringComparison.Ordinal);
    }
}
