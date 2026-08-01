using WinMatsch.Core;
using WinMatsch.Testing.Infrastructure;
using Xunit;

namespace WinMatsch.Cli.Tests;

/// <summary>
/// End-to-end checks of the real executable boundary: the published entry point running in a
/// separate process must honor the same exit-code and stream contract as the in-process host.
/// The CLI assembly copied next to the tests is launched through the dotnet muxer.
/// </summary>
public sealed class RealProcessTests
{
    [Fact]
    public async Task Version_succeeds_with_only_the_version_on_stdout()
    {
        ProcessResult result = await RunCliAsync("--version");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(
            CliVersion.InformationalVersion,
            result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Unknown_option_exits_2_with_concise_diagnostics_and_no_stack_trace()
    {
        ProcessResult result = await RunCliAsync("--nonsense");

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--nonsense", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_exposes_the_complete_production_command_root()
    {
        ProcessResult result = await RunCliAsync("--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("analyze", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("validate", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("show", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("list-versions", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("new", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("update", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("remove", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("submit", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("new-locale", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("update-locale", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("sync", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cleanup", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("complete", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("token", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("config", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cache", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("completion", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("config")]
    [InlineData("cache")]
    [InlineData("completion")]
    [InlineData("new")]
    [InlineData("update")]
    [InlineData("remove")]
    [InlineData("submit")]
    [InlineData("new-locale")]
    [InlineData("update-locale")]
    [InlineData("sync")]
    [InlineData("cleanup")]
    [InlineData("complete")]
    public async Task Every_production_command_help_loads_without_credentials_or_network(string command)
    {
        ProcessResult result = await RunCliAsync(command, "--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Completion_and_config_path_are_safe_without_credentials_or_network()
    {
        ProcessResult completion = await RunCliAsync("completion", "powershell");
        ProcessResult configPath = await RunCliAsync("config", "path");

        Assert.Equal(ExitCodes.Success, completion.ExitCode);
        Assert.Contains("Register-ArgumentCompleter", completion.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, completion.StandardError);
        Assert.Equal(ExitCodes.Success, configPath.ExitCode);
        Assert.Contains("config.yaml", configPath.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, configPath.StandardError);
    }

    [Fact]
    public async Task Local_analyze_runs_through_the_real_process_without_network()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "winmatsch.dll");
        string installer = Path.Combine(Path.GetTempPath(), $"winmatsch-{Guid.NewGuid():N}.exe");
        File.Copy(source, installer);
        try
        {
            ProcessResult result = await RunCliAsync(
                "analyze",
                installer,
                "--format",
                "json",
                "--interaction",
                "never");

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Contains("\"format\":\"portableExe\"", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            File.Delete(installer);
        }
    }

    [Fact]
    public async Task Local_offline_validate_runs_through_real_process_without_stack_trace()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "winmatsch-cli-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            WriteManifestSet(directory);

            ProcessResult result = await RunCliAsync(
                "validate",
                directory,
                "--offline",
                "--interaction",
                "never",
                "--format",
                "json");

            Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
            Assert.Contains("\"networkMode\":\"offline\"", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("\"code\":\"VLD6001\"", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] args)
    {
        string cliAssembly = Path.Combine(AppContext.BaseDirectory, "winmatsch.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly not found at '{cliAssembly}'.");

        var runner = new PhysicalProcessRunner();
        return await runner.RunAsync(new ProcessRequest
        {
            FileName = FindDotnetMuxer(),
            Arguments = [cliAssembly, .. args],
        });
    }

    private static string FindDotnetMuxer()
    {
        // The test host itself runs on the muxer; reuse it when possible so the test never
        // depends on PATH. Fall back to DOTNET_ROOT, then to PATH resolution.
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            string candidate = Path.Combine(
                dotnetRoot,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    private static void WriteManifestSet(string directory)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion("1.0.0");
        var locale = new LanguageTag("en-US");
        var manifests = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Exe,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://example.test/setup.exe",
                        InstallerSha256 = new Sha256Hash(
                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "Example App",
                License = "MIT",
                ShortDescription = "Example",
            },
            Locales = [],
        };

        foreach ((string name, string content) in PackageManifestIO.SerializeFiles(manifests))
        {
            File.WriteAllText(Path.Combine(directory, name), content);
        }
    }
}
