using System.Net;
using System.Security.Cryptography;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Testing.Infrastructure;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.GitHub;

public sealed class DownloaderFinalArtifactRevalidatorTests
{
    [Fact]
    public async Task Cleanup_failure_after_successful_safety_validation_is_nonblocking()
    {
        byte[] content = "stable installer"u8.ToArray();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
        using var downloader = new InstallerDownloader(handler);
        var scratch = new FailingCleanupScratchSpace();
        var revalidator = new DownloaderFinalArtifactRevalidator(downloader, scratch);
        GitHubSubmissionRequest request = RequestWithArtifact(content);

        FinalArtifactRevalidationResult result = await revalidator.RevalidateAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GH1021");
        Assert.True(Directory.Exists(scratch.Path));
        Directory.Delete(scratch.Path, recursive: true);
    }

    private static GitHubSubmissionRequest RequestWithArtifact(byte[] content)
    {
        LocalOperationPlan plan = GitHubLifecycleTestSupport.Plan();
        var download = new DownloadResult
        {
            FilePath = "planned.exe",
            FileName = "planned.exe",
            Sha256 = Sha256Hash.FromHashBytes(SHA256.HashData(content)),
            SizeInBytes = content.Length,
            RetrievedAt = DateTimeOffset.UtcNow,
            InitialUrl = "https://example.test/setup.exe",
            FinalUrl = "https://example.test/setup.exe",
        };
        return GitHubLifecycleTestSupport.Request() with
        {
            LocalPlan = plan with
            {
                Preflight = plan.Preflight with
                {
                    InstallerArtifacts =
                    [
                        new("https://example.test/setup.exe", download),
                    ],
                },
            },
        };
    }

    private sealed class FailingCleanupScratchSpace : IRevalidationScratchSpace
    {
        public string Path { get; private set; } = "";

        public string Create()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winmatsch-scratch-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            return Path;
        }

        public void Delete(string path)
            => throw new IOException("Synthetic cleanup failure.");
    }
}
