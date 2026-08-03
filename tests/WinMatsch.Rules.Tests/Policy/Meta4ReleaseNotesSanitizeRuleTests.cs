using WinMatsch.Core;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests.Policy;

public class Meta4ReleaseNotesSanitizeRuleTests
{
    private static PackageManifests CreateWithNotes(string? notes, string? url = null)
    {
        PackageManifests manifests = TestManifests.Create(TestManifests.CreateInstaller());
        manifests.DefaultLocale.ReleaseNotes = notes;
        manifests.DefaultLocale.ReleaseNotesUrl = url;
        return manifests;
    }

    [Fact]
    public void Leading_dash_bullets_become_bullet_characters()
    {
        // Motivating regression: '- ' bullets in block scalars stalled validation (mise #322416).
        PackageManifests manifests = CreateWithNotes("- Fixed crash\n- Improved startup\n  - nested item");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal("\u2022 Fixed crash\n\u2022 Improved startup\n  \u2022 nested item", manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Star_bullets_are_sanitized_too()
    {
        PackageManifests manifests = CreateWithNotes("* first\n* second");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal("\u2022 first\n\u2022 second", manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Bullet_transform_can_be_feature_flagged_off()
    {
        // The META-4-bullets subbehavior: cosmetic transform behind its own flag.
        PackageManifests manifests = CreateWithNotes("- Fixed crash");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule(sanitizeBullets: false).Apply(context);

        Assert.Equal("- Fixed crash", manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Key_value_looking_lines_get_a_fullwidth_colon()
    {
        PackageManifests manifests = CreateWithNotes("Breaking: the config format changed");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal("Breaking\uFF1Athe config format changed", manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Overlong_notes_are_truncated_at_a_paragraph_boundary()
    {
        string paragraph = new('a', 6_000);
        PackageManifests manifests = CreateWithNotes($"{paragraph}\n\n{paragraph}");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal(paragraph, manifests.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Overlong_notes_without_boundary_are_omitted_with_a_finding()
    {
        PackageManifests manifests = CreateWithNotes(new string('a', 12_000));
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Null(manifests.DefaultLocale.ReleaseNotes);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("omitted", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_prose_notes_are_untouched()
    {
        // Nonmatching control: no bullets, no key-value colon, short.
        PackageManifests manifests = CreateWithNotes("This release improves performance. See https://example.com for details.");
        ManifestContext context = TestManifests.CreateContext(manifests);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal(
            "This release improves performance. See https://example.com for details.",
            manifests.DefaultLocale.ReleaseNotes);
        Assert.Empty(context.Findings);
    }

    [Fact]
    public void Release_notes_url_is_retargeted_when_confirmed_evidence_exists()
    {
        // Motivating regression: wire ReleaseNotesUrl pointed at a different release (#189252, MAP-2 class).
        PackageManifests manifests = CreateWithNotes(null, "https://example.com/releases/1.0.0");
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        var rule = new Meta4ReleaseNotesSanitizeRule(new PolicyEvidence
        {
            ConfirmedUrls = ["https://example.com/releases/1.2.3"],
        });
        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        rule.Apply(context);

        Assert.Equal("https://example.com/releases/1.2.3", manifests.DefaultLocale.ReleaseNotesUrl);
    }

    [Fact]
    public void Stale_release_notes_url_without_evidence_only_produces_a_finding()
    {
        PackageManifests manifests = CreateWithNotes(null, "https://example.com/releases/1.0.0");
        PackageManifests previous = PolicyTestSupport.CreatePrevious("1.0.0", TestManifests.CreateInstaller());
        ManifestContext context = TestManifests.CreateContext(manifests, previous: previous);

        new Meta4ReleaseNotesSanitizeRule().Apply(context);

        Assert.Equal("https://example.com/releases/1.0.0", manifests.DefaultLocale.ReleaseNotesUrl);
        RuleFinding finding = Assert.Single(context.Findings);
        Assert.Contains("previous version", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_only_mode_proposes_without_mutating()
    {
        PackageManifests manifests = CreateWithNotes("- Fixed crash");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta4ReleaseNotesSanitizeRule(), manifests, RuleMode.LogOnly);

        Assert.Equal("- Fixed crash", manifests.DefaultLocale.ReleaseNotes);
        Assert.Contains(context.Changes, c => c.Mode == RuleMode.LogOnly);
    }

    [Fact]
    public void Disabled_mode_does_nothing()
    {
        PackageManifests manifests = CreateWithNotes("- Fixed crash");

        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta4ReleaseNotesSanitizeRule(), manifests, RuleMode.Disabled);

        Assert.Equal("- Fixed crash", manifests.DefaultLocale.ReleaseNotes);
        Assert.Empty(context.Changes);
    }

    [Fact]
    public void Dedicated_bullet_rule_logs_its_independent_runtime_id()
    {
        PackageManifests manifests = CreateWithNotes("- Fixed crash");
        ManifestContext context = PolicyTestSupport.RunViaPipeline(
            new Meta4ReleaseNotesBulletRule(),
            manifests,
            RuleMode.Apply);

        Assert.Equal("\u2022 Fixed crash", manifests.DefaultLocale.ReleaseNotes);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal(RuleCatalogueIds.Meta4Bullets, change.RuleId);
        Assert.Contains("bullet", change.SourceEvidence, StringComparison.OrdinalIgnoreCase);
    }
}
