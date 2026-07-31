using WinMatsch.Testing.Fixtures;
using Xunit;

namespace WinMatsch.Testing.Tests;

public sealed class FixtureCatalogTests
{
    private static readonly string[] _expectedFixtureIds =
    [
        "buf",
        "clouddrive2",
        "curl",
        "electron",
        "exiftool",
        "keeper-commander",
        "mise",
        "notesnook",
        "pandoc",
        "sonarr",
        "super-productivity",
        "surrealdb",
        "uhk-agent",
    ];

    [Fact]
    public void Catalog_contains_the_complete_named_regression_corpus()
    {
        string[] ids = FixtureCatalog.All
            .Select(fixture => fixture.Descriptor.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(_expectedFixtureIds, ids);
    }

    [Fact]
    public void Included_assets_match_the_expected_manifest_snapshot()
    {
        foreach (RegressionFixture fixture in FixtureCatalog.All)
        {
            var expectedAssets = fixture.Expected.Installers
                .Select(installer => (installer.InstallerUrl, installer.InstallerSha256))
                .ToHashSet();

            foreach (FixtureAsset asset in fixture.Descriptor.Assets)
            {
                bool isExpected = expectedAssets.Contains((asset.Url, asset.Sha256));
                Assert.True(
                    asset.IncludeInExpectedManifest == isExpected,
                    $"Fixture '{fixture.Descriptor.Id}' asset '{asset.FileName}' "
                    + $"has IncludeInExpectedManifest={asset.IncludeInExpectedManifest} "
                    + $"but expected snapshot membership is {isExpected}.");
            }
        }
    }

    [Fact]
    public void Provenance_is_https_and_commit_pinned()
    {
        foreach (RegressionFixture fixture in FixtureCatalog.All)
        {
            FixtureProvenance provenance = fixture.Descriptor.Provenance;

            Assert.Equal(Uri.UriSchemeHttps, provenance.ManifestUrl.Scheme);
            Assert.Contains(provenance.HeadCommit, provenance.ManifestUrl.AbsoluteUri);
            Assert.Equal(40, provenance.HeadCommit.Length);
            Assert.Equal(40, provenance.MergeCommit.Length);
            Assert.NotEqual(default, provenance.ObservedAt);
        }
    }

    [Fact]
    public void Regression_specific_expected_shapes_are_preserved()
    {
        RegressionFixture uhk = FixtureCatalog.Get("uhk-agent");
        Assert.Equal(2, uhk.Expected.Installers.Count);
        Assert.Contains(
            uhk.Descriptor.Assets,
            asset => !asset.IncludeInExpectedManifest && asset.FileName.EndsWith("-win.exe", StringComparison.Ordinal));

        RegressionFixture pandoc = FixtureCatalog.Get("pandoc");
        Assert.Equal(["user", "machine"], pandoc.Expected.Installers.Select(installer => installer.Scope));

        RegressionFixture surrealDb = FixtureCatalog.Get("surrealdb");
        Assert.Single(surrealDb.Expected.Installers.Select(installer => installer.InstallerUrl).Distinct());
        Assert.Equal(["x64", "x86"], surrealDb.Expected.Installers.Select(installer => installer.Architecture));

        Assert.All(
            FixtureCatalog.Get("clouddrive2").Expected.AppsAndFeaturesEntries,
            entry => Assert.Null(entry.DisplayVersion));
        Assert.All(
            FixtureCatalog.Get("sonarr").Expected.AppsAndFeaturesEntries,
            entry => Assert.Null(entry.DisplayVersion));

        string releaseNotes = FixtureCatalog.Get("mise").Expected.Locale?.ReleaseNotes
            ?? throw new InvalidDataException("mise release notes are missing.");
        Assert.Contains("\u2022 ", releaseNotes);
        Assert.DoesNotContain("\n- ", releaseNotes);
    }

    [Fact]
    public void Sanitized_recordings_exclude_credentials_and_cover_all_assets()
    {
        IReadOnlyList<HttpInteractionRecording> recordings = FixtureCatalog.LoadRecordings();
        string serialized = string.Join(
            "\n",
            recordings.Select(
                recording =>
                    $"{recording.Method} {recording.Uri} "
                    + $"{string.Join(' ', recording.ResponseHeaders)} "
                    + $"{recording.Body?.GetRawText()}"));

        Assert.NotEmpty(recordings);
        Assert.DoesNotContain("authorization", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            recordings,
            recording =>
            {
                Assert.Equal(Uri.UriSchemeHttps, recording.Uri.Scheme);
                Assert.All(
                    recording.ResponseHeaders.Keys,
                    name => Assert.Equal("Content-Type", name, ignoreCase: true));
            });

        foreach (FixtureAsset asset in FixtureCatalog.All.SelectMany(fixture => fixture.Descriptor.Assets))
        {
            Assert.Contains(asset.Url.AbsoluteUri, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Recorded_handler_replays_sanitized_GitHub_response()
    {
        IReadOnlyList<HttpInteractionRecording> recordings = FixtureCatalog.LoadRecordings();
        HttpInteractionRecording recording = recordings.First(item => item.Id == "electron-release");
        var handler = new RecordedHttpMessageHandler(recordings);
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(recording.Uri);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("win32-arm64", body);
        Assert.Single(handler.Requests);
    }
}
