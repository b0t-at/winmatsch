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
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(temporary.Path);
        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Compiler corpus failed.{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");

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
