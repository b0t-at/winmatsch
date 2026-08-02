using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using WinMatsch.Analysis.Tests;
using WinMatsch.Cli;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.E2E.Tests;

public sealed class InstallerCorpusProcessTests
{
    public static TheoryData<string, string> Corpus =>
        new()
        {
            { "fixture.msi", "msi" },
            { "fixture.msix", "msix" },
            { "fixture.zip", "zip" },
            { "burn.exe", "burn" },
            { "nsis.exe", "nullsoft" },
            { "inno.exe", "innoSetup" },
            { "advanced.exe", "advancedInstaller" },
            { "squirrel.exe", "squirrel" },
        };

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Real_process_analyzes_deterministic_installer_corpus(
        string fileName,
        string expectedFormat)
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, fileName);
        await File.WriteAllBytesAsync(path, Build(fileName));

        ProcessResult result = await CliProcess.RunAsync(
            ["analyze", path, "--format", "json", "--interaction", "never", "--no-color"]);

        Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"Exit {result.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}"
            + $"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
        Assert.Contains($"\"format\":\"{expectedFormat}\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        CliProcess.AssertSafe(result);
    }

    [Theory]
    [InlineData("json", null, null)]
    [InlineData("text", "CI", "true")]
    [InlineData("text", "NO_COLOR", "1")]
    [InlineData("text", null, null)]
    public async Task Real_process_analyze_keeps_noninteractive_stderr_clean(
        string format,
        string? environmentName,
        string? environmentValue)
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "fixture.msi");
        await File.WriteAllBytesAsync(path, Build("fixture.msi"));
        IReadOnlyDictionary<string, string?>? environment = environmentName is null
            ? null
            : new Dictionary<string, string?> { [environmentName] = environmentValue };

        ProcessResult result = await CliProcess.RunAsync(
            ["analyze", path, "--format", format],
            environment);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        CliProcess.AssertSafe(result);
    }

    [Fact]
    public async Task Real_process_help_version_and_every_command_help_are_hermetic()
    {
        string[] commands =
        [
            "analyze",
            "validate",
            "show",
            "list-versions",
            "new",
            "update",
            "remove",
            "submit",
            "new-locale",
            "update-locale",
            "sync",
            "cleanup",
            "complete",
            "token",
            "config",
            "cache",
            "completion",
        ];

        ProcessResult root = await CliProcess.RunAsync(["--help", "--no-color"]);
        ProcessResult version = await CliProcess.RunAsync(["--version"]);
        Assert.Equal(ExitCodes.Success, root.ExitCode);
        Assert.Equal(ExitCodes.Success, version.ExitCode);
        Assert.Equal(CliVersion.InformationalVersion, version.StandardOutput.Trim());
        foreach (string command in commands)
        {
            Assert.Contains(command, root.StandardOutput, StringComparison.Ordinal);
            ProcessResult help = await CliProcess.RunAsync([command, "--help", "--no-color"]);
            Assert.Equal(ExitCodes.Success, help.ExitCode);
            Assert.Contains("Usage:", help.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(string.Empty, help.StandardError);
            CliProcess.AssertSafe(help);
        }

        CliProcess.AssertSafe(root);
        CliProcess.AssertSafe(version);
    }

    [Fact]
    public async Task Real_process_maintenance_dry_runs_are_zero_mutation()
    {
        using var temporary = new TemporaryDirectory();
        string config = Path.Combine(temporary.Path, "config.yaml");
        string cache = Path.Combine(temporary.Path, "cache");
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(config, "");
        IReadOnlyDictionary<string, string?> environment =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["WINMATSCH_CACHE_DIRECTORY"] = cache,
            };

        ProcessResult set = await CliProcess.RunAsync(
            [
                "config",
                "set",
                "repository",
                "owner/repo",
                "--config",
                config,
                "--dry-run",
                "--format",
                "json",
                "--interaction",
                "never",
                "--no-color",
            ],
            environment);
        ProcessResult clear = await CliProcess.RunAsync(
            ["cache", "clear", "--dry-run", "--format", "json", "--interaction", "never", "--no-color"],
            environment);
        ProcessResult completion = await CliProcess.RunAsync(
            ["completion", "powershell", "--no-color"],
            environment);

        Assert.Equal(ExitCodes.Success, set.ExitCode);
        Assert.Equal(ExitCodes.Success, clear.ExitCode);
        Assert.Equal(ExitCodes.Success, completion.ExitCode);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(config));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        Assert.Contains("\"applied\":false", set.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"applied\":false", clear.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Register-ArgumentCompleter", completion.StandardOutput, StringComparison.Ordinal);
        CliProcess.AssertSafe(set);
        CliProcess.AssertSafe(clear);
        CliProcess.AssertSafe(completion);
    }

    [Fact]
    public async Task Real_process_destructive_dry_run_and_offline_validation_never_mutate()
    {
        using var temporary = new TemporaryDirectory();
        WriteProcessPackage(temporary.Path);
        Dictionary<string, byte[]> before = Snapshot(temporary.Path);
        string versionDirectory = Path.Combine(
            temporary.Path,
            ManifestPaths.GetVersionDirectory(
                    new PackageIdentifier("Example.Process"),
                    new PackageVersion("1.0.0"))
                .Replace('/', Path.DirectorySeparatorChar));

        ProcessResult remove = await CliProcess.RunAsync(
        [
            "remove",
            "Example.Process",
            "1.0.0",
            "--output",
            temporary.Path,
            "--dry-run",
            "--format",
            "json",
            "--interaction",
            "never",
            "--no-color",
        ]);
        ProcessResult validate = await CliProcess.RunAsync(
        [
            "validate",
            versionDirectory,
            "--offline",
            "--format",
            "json",
            "--interaction",
            "never",
            "--no-color",
        ]);
        Assert.Equal(ExitCodes.Success, remove.ExitCode);
        Assert.Equal(ExitCodes.OperationFailed, validate.ExitCode);
        Assert.Contains("\"applied\":false", remove.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"networkMode\":\"offline\"", validate.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"VLD6001\"", validate.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(before.Keys, Snapshot(temporary.Path).Keys);
        foreach ((string path, byte[] bytes) in before)
        {
            Assert.Equal(bytes, Snapshot(temporary.Path)[path]);
        }

        CliProcess.AssertSafe(remove);
        CliProcess.AssertSafe(validate);
    }

    [Fact]
    public async Task Real_process_parse_failure_never_echoes_injected_realistic_secrets()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string ghp = $"ghp_{nonce}ABCDEFGH";
        string githubPat = $"github_pat_{nonce}_ABCDEFGH";
        string jwt = $"eyJhbGciOiJIUzI1NiJ9.eyJqdGkiOiI{nonce}In0.{nonce}signature";
        string presigned =
            $"https://downloads.invalid/setup.exe?X-Amz-Credential={nonce}%2Fscope"
            + $"&X-Amz-Signature={nonce}signature";

        ProcessResult result = await CliProcess.RunAsync(
        [
            "new",
            "Example.SecretSafety",
            "--token",
            ghp,
            "--url",
            $"{presigned}|x64||",
            "--prtitle",
            jwt,
            "--created-with",
            githubPat,
            "--not-a-real-option",
            "--format",
            "json",
            "--interaction",
            "never",
            "--no-color",
        ]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        CliProcess.AssertSafe(result, ghp, githubPat, jwt, nonce);
    }

    private static byte[] Build(string fileName) => fileName switch
    {
        "fixture.msi" => MsiFixtures.BuildMsi(
        [
            ("ProductName", "Contoso App"),
            ("ProductVersion", "2.5.0"),
            ("Manufacturer", "Contoso Ltd"),
            ("ProductCode", "{11111111-2222-3333-4444-555555555555}"),
        ]),
        "fixture.msix" => MsixFixtures.BuildPackage(MsixFixtures.PackageManifest()).ToArray(),
        "fixture.zip" => BuildZip(),
        "burn.exe" => BurnFixtures.BuildBundle(BurnFixtures.ManifestXml()),
        "nsis.exe" => NsisFixtures.BuildInstaller(),
        "inno.exe" => InnoFixtures.BuildInstaller(),
        "advanced.exe" => AdvancedInstallerFixtures.BuildInstaller(
        [
            ("ProductName", "Contoso Studio"),
            ("ProductVersion", "3.1.0"),
            ("Manufacturer", "Contoso Ltd"),
            ("ProductCode", "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"),
        ]),
        "squirrel.exe" => SquirrelFixtures.BuildClassicSetup(
            SquirrelFixtures.BuildNupkg(SquirrelFixtures.NuspecXml())),
        _ => throw new ArgumentOutOfRangeException(nameof(fileName)),
    };

    private static byte[] BuildZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("contoso.exe");
            using Stream destination = entry.Open();
            destination.Write(PeFixtures.BuildExe(
                Machine.Amd64,
                new VersionStrings(
                    ProductName: "Contoso Portable",
                    CompanyName: "Contoso Ltd",
                    ProductVersion: "1.0.0")));
        }

        return stream.ToArray();
    }

    private static void WriteProcessPackage(string root)
    {
        var package = new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = new PackageIdentifier("Example.Process"),
                PackageVersion = new PackageVersion("1.0.0"),
                DefaultLocale = new LanguageTag("en-US"),
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = new PackageIdentifier("Example.Process"),
                PackageVersion = new PackageVersion("1.0.0"),
                InstallerType = InstallerType.Exe,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://fixtures.invalid/process.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = new PackageIdentifier("Example.Process"),
                PackageVersion = new PackageVersion("1.0.0"),
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "Example",
                PackageName = "Process",
                License = "MIT",
                ShortDescription = "Process fixture",
            },
            Locales = [],
        };
        string directory = Path.Combine(
            root,
            ManifestPaths.GetVersionDirectory(
                    new PackageIdentifier("Example.Process"),
                    new PackageVersion("1.0.0"))
                .Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach ((string fileName, string content) in PackageManifestIO.SerializeFiles(package))
        {
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);
}
