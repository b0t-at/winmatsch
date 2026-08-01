using System.Globalization;
using System.Security.Cryptography;
using WinMatsch.Cli.Commands.Maintenance;
using WinMatsch.Cli.Tests.Harness;
using WinMatsch.Core;
using WinMatsch.Downloads;
using Xunit;

namespace WinMatsch.Cli.Tests.Maintenance;

public sealed class CacheCommandTests : IDisposable
{
    private const string EntryUrl = "https://example.invalid/downloads/app-1.0.0.exe";

    private readonly string _cacheDirectory;
    private readonly string _scratchDirectory;
    private readonly FakeTime _time = new();

    public CacheCommandTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "winmatsch-cache-tests", Guid.NewGuid().ToString("N"));
        _cacheDirectory = Path.Combine(root, "cache");
        _scratchDirectory = Path.Combine(root, "scratch");
        Directory.CreateDirectory(_scratchDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_cacheDirectory)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task List_reports_an_empty_cache()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "list"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Entries: 0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(_cacheDirectory, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_reports_state_size_and_lifetime()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult text = await harness.RunAsync(["cache", "list"]);
        CliRunResult json = await harness.RunAsync(["cache", "list", "--format", "json"]);

        Assert.Equal(ExitCodes.Success, text.ExitCode);
        Assert.Contains("[fresh]", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("13 bytes", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(EntryUrl, text.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, json.ExitCode);
        Assert.Contains("\"state\":\"fresh\"", json.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"sizeInBytes\":13", json.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_shows_one_entry_and_fails_for_unknown_urls()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult found = await harness.RunAsync(["cache", "inspect", EntryUrl]);
        CliRunResult missing = await harness.RunAsync(["cache", "inspect", "https://example.invalid/other.exe"]);

        Assert.Equal(ExitCodes.Success, found.ExitCode);
        Assert.Contains("State: fresh", found.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Sha256: ", found.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.OperationFailed, missing.ExitCode);
        Assert.Contains("No cache entry exists", missing.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_reports_corrupt_entries()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        TamperWithPayload();
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "inspect", EntryUrl]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("State: corrupt", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_rejects_traversal_shaped_input()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "inspect", "..\\..\\outside"]);

        Assert.Equal(ExitCodes.UsageError, result.ExitCode);
        Assert.Contains("absolute http(s) URL", result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(SoleMetadataPath()));
    }

    [Fact]
    public async Task Clear_dry_run_removes_nothing()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "clear", "--dry-run"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Would remove 1 entry", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("dry run", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(SoleMetadataPath()));
    }

    [Fact]
    public async Task Clear_requires_confirmation_and_honors_decline()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();
        harness.Interaction.EnqueueConfirm(false);

        CliRunResult result = await harness.RunAsync(["cache", "clear"]);

        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Contains("confirmation declined", result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(SoleMetadataPath()));
    }

    [Fact]
    public async Task Clear_without_a_tty_and_without_yes_is_missing_input()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();
        harness.IsInputRedirected = true;

        CliRunResult result = await harness.RunAsync(["cache", "clear"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Contains("--yes", result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(SoleMetadataPath()));
    }

    [Fact]
    public async Task Clear_in_json_mode_never_prompts()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "clear", "--format", "json"]);

        Assert.Equal(ExitCodes.MissingInput, result.ExitCode);
        Assert.Empty(harness.Interaction.Questions);
        Assert.True(File.Exists(SoleMetadataPath()));
    }

    [Fact]
    public async Task Clear_with_yes_removes_everything()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "clear", "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Removed 1 entry", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_cacheDirectory, "*.json"));
    }

    [Fact]
    public async Task Clear_by_url_removes_only_that_entry()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        await StoreEntryAsync("https://example.invalid/other-2.0.0.exe", "other-bytes");
        CliHarness harness = CreateHarness();
        harness.Interaction.EnqueueConfirm(true);

        CliRunResult result = await harness.RunAsync(["cache", "clear", EntryUrl]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Single(Directory.EnumerateFiles(_cacheDirectory, "*.json"));
    }

    [Fact]
    public async Task Clear_for_an_unknown_url_is_idempotent()
    {
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "clear", EntryUrl, "--yes"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Removed 0 entries", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_removes_stale_and_corrupt_entries_and_keeps_fresh_ones()
    {
        await StoreEntryAsync("https://example.invalid/stale-1.exe", "stale-bytes");
        _time.Advance(TimeSpan.FromDays(8));
        await StoreEntryAsync(EntryUrl, "fresh-bytes");
        await StoreEntryAsync("https://example.invalid/corrupt-1.exe", "corrupt-bytes");
        TamperWithPayload("corrupt-1");
        CliHarness harness = CreateHarness();

        CliRunResult dryRun = await harness.RunAsync(["cache", "prune", "--dry-run"]);
        CliRunResult applied = await harness.RunAsync(["cache", "prune", "--yes"]);
        CliRunResult after = await harness.RunAsync(["cache", "list"]);

        Assert.Equal(ExitCodes.Success, dryRun.ExitCode);
        Assert.Contains("Would remove 2 entries", dryRun.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, applied.ExitCode);
        Assert.Contains("Removed 2 entries", applied.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Entries: 1", after.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(EntryUrl, after.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_with_nothing_to_remove_never_prompts()
    {
        await StoreEntryAsync(EntryUrl, "payload-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult result = await harness.RunAsync(["cache", "prune"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(harness.Interaction.Questions);
        Assert.Contains("Removed 0 entries", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_list_and_prune_stay_consistent()
    {
        for (int index = 0; index < 4; index++)
        {
            await StoreEntryAsync(
                $"https://example.invalid/app-{index.ToString(CultureInfo.InvariantCulture)}.exe",
                $"bytes-{index.ToString(CultureInfo.InvariantCulture)}");
        }

        _time.Advance(TimeSpan.FromDays(8));
        await StoreEntryAsync(EntryUrl, "fresh-bytes");
        CliHarness pruneHarness = CreateHarness();
        CliHarness listHarness = CreateHarness();

        Task<CliRunResult> pruneTask = pruneHarness.RunAsync(["cache", "prune", "--yes"]);
        Task<CliRunResult> listTask = listHarness.RunAsync(["cache", "list"]);
        CliRunResult prune = await pruneTask;
        CliRunResult list = await listTask;

        Assert.Equal(ExitCodes.Success, prune.ExitCode);
        Assert.Equal(ExitCodes.Success, list.ExitCode);
        CliRunResult after = await CreateHarness().RunAsync(["cache", "list"]);
        Assert.Contains("Entries: 1", after.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Urls_with_embedded_credentials_are_redacted_in_output()
    {
        const string signedUrl = "https://user:s3cretpass@example.invalid/app.exe?sig=SECRETSIG&token=SECRETTOK";
        await StoreEntryAsync(signedUrl, "signed-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult list = await harness.RunAsync(["cache", "list"]);
        CliRunResult inspect = await harness.RunAsync(["cache", "inspect", signedUrl]);
        CliRunResult json = await harness.RunAsync(["cache", "list", "--format", "json"]);
        CliRunResult missing = await harness.RunAsync(
            ["cache", "inspect", "https://example.invalid/gone.exe?token=SECRETTOK"]);

        foreach (CliRunResult result in new[] { list, inspect, json, missing })
        {
            Assert.DoesNotContain("s3cretpass", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETSIG", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETTOK", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cretpass", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETSIG", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETTOK", result.StandardError, StringComparison.Ordinal);
        }

        Assert.Contains("[REDACTED]", list.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, inspect.ExitCode);
        Assert.Equal(ExitCodes.OperationFailed, missing.ExitCode);
    }

    [Fact]
    public async Task Urls_with_username_only_credentials_are_redacted()
    {
        const string tokenUrl = "https://ghp_bareTokenUser@example.invalid/app.exe";
        await StoreEntryAsync(tokenUrl, "token-bytes");
        CliHarness harness = CreateHarness();

        CliRunResult list = await harness.RunAsync(["cache", "list"]);
        CliRunResult json = await harness.RunAsync(["cache", "list", "--format", "json"]);

        foreach (CliRunResult result in new[] { list, json })
        {
            Assert.DoesNotContain("ghp_bareTokenUser", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_bareTokenUser", result.StandardError, StringComparison.Ordinal);
        }

        Assert.Contains("[REDACTED]@example.invalid", list.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cache_commands_reject_empty_urls()
    {
        CliHarness harness = CreateHarness();

        CliRunResult inspect = await harness.RunAsync(["cache", "inspect", " "]);
        CliRunResult clear = await harness.RunAsync(["cache", "clear", " ", "--yes"]);

        Assert.Equal(ExitCodes.UsageError, inspect.ExitCode);
        Assert.Equal(ExitCodes.UsageError, clear.ExitCode);
    }

    private CliHarness CreateHarness()
    {
        var harness = new CliHarness();
        harness.EnvironmentVariables["WINMATSCH_CACHE_DIRECTORY"] = _cacheDirectory;
        harness.Modules.Add(new CacheCommandModule(CreateCache));
        return harness;
    }

    private DownloadCache CreateCache(string directory)
        => new(directory, new DownloadCacheOptions { TimeProvider = _time });

    private async Task StoreEntryAsync(string url, string content)
    {
        string fileName = new Uri(url).Segments[^1];
        string filePath = Path.Combine(_scratchDirectory, fileName);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(filePath, bytes);
        var result = new DownloadResult
        {
            FilePath = filePath,
            FileName = fileName,
            Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
            SizeInBytes = bytes.Length,
            InitialUrl = url,
            FinalUrl = url,
            RetrievedAt = _time.GetUtcNow(),
        };
        await CreateCache(_cacheDirectory).StoreAsync(result);
    }

    private void TamperWithPayload(string? nameFragment = null)
    {
        foreach (string payloadPath in Directory.EnumerateFiles(_cacheDirectory, "*.payload"))
        {
            if (nameFragment is null || MetadataMentions(payloadPath, nameFragment))
            {
                File.WriteAllText(payloadPath, "tampered");
            }
        }
    }

    private bool MetadataMentions(string payloadPath, string nameFragment)
    {
        string payloadFileName = Path.GetFileName(payloadPath);
        return Directory.EnumerateFiles(_cacheDirectory, "*.json")
            .Any(metadataPath =>
            {
                string metadata = File.ReadAllText(metadataPath);
                return metadata.Contains(payloadFileName, StringComparison.Ordinal)
                    && metadata.Contains(nameFragment, StringComparison.Ordinal);
            });
    }

    private string SoleMetadataPath()
        => Directory.EnumerateFiles(_cacheDirectory, "*.json").Single();

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
