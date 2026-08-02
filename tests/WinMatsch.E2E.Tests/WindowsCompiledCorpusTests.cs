using System.Diagnostics;
using WinMatsch.Analysis;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class WindowsCompiledCorpusTests
{
    [WindowsEnvironmentFact("WINMATSCH_E2E_COMPILE_WINDOWS_FIXTURES", "1")]
    public async Task Signed_pinned_windows_compilers_produce_real_analyzable_fixture_formats()
    {
        using var temporary = new TemporaryDirectory();
        string script = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "WinMatsch.Testing",
            "Tools",
            "Build-WindowsInstallerCorpus.ps1");
        ProcessResult result = await RunPowerShellAsync(
            script,
            "-OutputDirectory",
            temporary.Path);
        Assert.True(
            result.ExitCode == 0,
            $"Compiler corpus failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
        Assert.Equal(
            5,
            result.StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Count(static line => line.StartsWith("PROVENANCE ", StringComparison.Ordinal)));
        Assert.Contains(
            "constraint=valid-microsoft-authenticode-sdk-build-26100",
            result.StandardOutput,
            StringComparison.Ordinal);

        AssertFormat("fixture-inno.exe", DetectedInstallerFormat.InnoSetup, InstallerType.Inno);
        AssertFormat("fixture-nsis.exe", DetectedInstallerFormat.Nullsoft, InstallerType.Nullsoft);
        AssertFormat("fixture-wix.msi", DetectedInstallerFormat.Msi, InstallerType.Wix);
        AssertFormat("fixture-msi.msi", DetectedInstallerFormat.Msi, InstallerType.Wix);
        AssertFormat("fixture-burn.exe", DetectedInstallerFormat.Burn, InstallerType.Burn);
        AssertFormat("fixture.msix", DetectedInstallerFormat.Msix, InstallerType.Msix);

        void AssertFormat(
            string fileName,
            DetectedInstallerFormat expectedFormat,
            InstallerType expectedType)
        {
            InstallerAnalysis analysis = FileAnalyzer.AnalyzeFile(Path.Combine(temporary.Path, fileName));
            Assert.Equal(expectedFormat, analysis.Format);
            Assert.Contains(
                analysis.Installers,
                installer => installer.InstallerType == expectedType);
        }
    }

    [Fact]
    public async Task Tooling_resolver_fallbacks_and_validation_are_fail_closed()
    {
        string script = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "WinMatsch.Testing",
            "Tools",
            "WindowsInstallerCorpus.Tooling.Tests.ps1");

        ProcessResult result = await RunPowerShellAsync(script);

        Assert.True(
            result.ExitCode == 0,
            $"Tooling tests failed.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
        Assert.Contains(
            "Windows installer corpus tooling tests passed.",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string script,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"PowerShell script '{script}' could not be started.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinMatsch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
