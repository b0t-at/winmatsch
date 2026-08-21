using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Meta3GitHubLicenseUrlRuleTests
{
    private static readonly Meta3GitHubLicenseUrlRule _rule = new();

    private static PackageManifests CreateWithLicenseUrl(string? licenseUrl, string? copyrightUrl = null)
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.LicenseUrl = licenseUrl;
        manifests.DefaultLocale.CopyrightUrl = copyrightUrl;
        return manifests;
    }

    [Fact]
    public void Commit_pinned_blob_url_is_normalized_to_head_when_confirmed()
    {
        // Motivating regression: GitButler blob/7d01a53.../LICENSE.md -> stable link (#162317).
        var rule = new Meta3GitHubLicenseUrlRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://github.com/gitbutlerapp/gitbutler/blob/HEAD/LICENSE.md"],
        });
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://github.com/gitbutlerapp/gitbutler/blob/7d01a53a15bcf5c0e0c4e0d5cbf4b0a1e4a01234/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(
            "https://github.com/gitbutlerapp/gitbutler/blob/HEAD/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Commit_pinned_blob_url_is_kept_without_head_confirmation()
    {
        // Motivating regression: nvQuickSite's tag-pinned license existed while blob/HEAD
        // was a hard 404 (the file moved at HEAD); PR #419632 shipped the dead rewrite.
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://github.com/gitbutlerapp/gitbutler/blob/7d01a53a15bcf5c0e0c4e0d5cbf4b0a1e4a01234/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/gitbutlerapp/gitbutler/blob/7d01a53a15bcf5c0e0c4e0d5cbf4b0a1e4a01234/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Raw_githubusercontent_url_is_normalized_to_blob_head_when_confirmed()
    {
        // Motivating regression: Authme raw.githubusercontent.com/...main/LICENSE.md -> blob/HEAD (#197643).
        var rule = new Meta3GitHubLicenseUrlRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://github.com/Levminer/authme/blob/HEAD/LICENSE.md"],
        });
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/Levminer/authme/main/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(
            "https://github.com/Levminer/authme/blob/HEAD/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Raw_githubusercontent_url_keeps_its_pinned_ref_without_head_confirmation()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/Levminer/authme/v1.2.3/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/Levminer/authme/blob/v1.2.3/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Raw_githubusercontent_refs_heads_url_normalizes_without_duplicating_the_ref_segment()
    {
        // Motivating regression: trellis-lab/trellis raw.githubusercontent.com/.../refs/heads/main/EULA.md
        // was rewritten to blob/HEAD/heads/main/EULA.md because the ref-capturing regex only
        // consumed the single "refs" segment, leaving "heads/main" glued to the front of <path>
        // (PR #419103).
        var rule = new Meta3GitHubLicenseUrlRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://github.com/trellis-lab/trellis/blob/HEAD/EULA.md"],
        });
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/trellis-lab/trellis/refs/heads/main/EULA.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(
            "https://github.com/trellis-lab/trellis/blob/HEAD/EULA.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Raw_githubusercontent_refs_heads_url_keeps_its_pinned_ref_without_head_confirmation()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/trellis-lab/trellis/refs/heads/main/EULA.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/trellis-lab/trellis/blob/main/EULA.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Raw_githubusercontent_refs_tags_url_keeps_its_pinned_ref_without_head_confirmation()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/owner/repo/refs/tags/v1.2.3/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/owner/repo/blob/v1.2.3/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void GitHub_raw_shorthand_url_is_normalized_to_blob_head_when_confirmed()
    {
        // Motivating regression: ZacharyL2/KeyEcho github.com/.../raw/refs/heads/main/LICENSE
        // (PR #419628) - the "github.com/{owner}/{repo}/raw/{ref}/{path}" shorthand redirects
        // to raw content but was never recognized/normalized by this rule at all.
        var rule = new Meta3GitHubLicenseUrlRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://github.com/ZacharyL2/KeyEcho/blob/HEAD/LICENSE"],
        });
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://github.com/ZacharyL2/KeyEcho/raw/refs/heads/main/LICENSE");
        ManifestContext context = TestManifests.CreateContext(manifests);

        rule.Apply(context);

        Assert.Equal(
            "https://github.com/ZacharyL2/KeyEcho/blob/HEAD/LICENSE",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void GitHub_raw_shorthand_url_keeps_its_pinned_ref_without_head_confirmation()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://github.com/ZacharyL2/KeyEcho/raw/refs/heads/main/LICENSE");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/ZacharyL2/KeyEcho/blob/main/LICENSE",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void GitHub_raw_shorthand_url_with_plain_ref_keeps_its_pinned_ref_without_head_confirmation()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://github.com/owner/repo/raw/v1.2.3/LICENSE.md");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal(
            "https://github.com/owner/repo/blob/v1.2.3/LICENSE.md",
            manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Copyright_url_is_normalized_too()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            null,
            "https://raw.githubusercontent.com/owner/repo/v1.2.3/COPYING");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("https://github.com/owner/repo/blob/v1.2.3/COPYING", manifests.DefaultLocale.CopyrightUrl);
    }

    [Fact]
    public void Branch_named_blob_urls_are_left_alone()
    {
        // Conservative: renaming branch links is the publisher's call.
        PackageManifests manifests = CreateWithLicenseUrl("https://github.com/owner/repo/blob/master/LICENSE");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("https://github.com/owner/repo/blob/master/LICENSE", manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Short_hex_refs_are_left_alone()
    {
        // A 7-hex ref could legally be a branch name; only unambiguous full-40-hex commit
        // pins are normalized.
        PackageManifests manifests = CreateWithLicenseUrl("https://github.com/owner/repo/blob/cafe123/LICENSE");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("https://github.com/owner/repo/blob/cafe123/LICENSE", manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Non_github_urls_are_untouched()
    {
        // Nonmatching control.
        PackageManifests manifests = CreateWithLicenseUrl("https://example.com/legal/license.html");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("https://example.com/legal/license.html", manifests.DefaultLocale.LicenseUrl);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Already_head_pinned_urls_are_untouched()
    {
        PackageManifests manifests = CreateWithLicenseUrl("https://github.com/owner/repo/blob/HEAD/LICENSE");
        ManifestContext context = TestManifests.CreateContext(manifests);

        _rule.Apply(context);

        Assert.Equal("https://github.com/owner/repo/blob/HEAD/LICENSE", manifests.DefaultLocale.LicenseUrl);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/owner/repo/main/LICENSE");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.LogOnly);

        Assert.Equal(
            "https://raw.githubusercontent.com/owner/repo/main/LICENSE",
            manifests.DefaultLocale.LicenseUrl);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = CreateWithLicenseUrl(
            "https://raw.githubusercontent.com/owner/repo/main/LICENSE");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(_rule, manifests, RuleMode.Disabled);

        Assert.Equal(
            "https://raw.githubusercontent.com/owner/repo/main/LICENSE",
            manifests.DefaultLocale.LicenseUrl);
        Assert.Empty(context.Changes);
    }
}
