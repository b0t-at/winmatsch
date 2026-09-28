using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;
using Xunit;

namespace WinMatsch.Workflows.Tests.Operations;

public sealed class UpstreamVerdictGateTests
{
    private static readonly PackageIdentifier _identifier = new("Example.App");
    private static readonly Dictionary<string, PackageManifests> _noRejectedVersions = [];

    [Theory]
    [InlineData(FeedbackClassification.ScannerBlocked, "1.0.0")]
    [InlineData(FeedbackClassification.InstallerUnavailable, "1.0.0")]
    [InlineData(FeedbackClassification.InstallationFailure, "1.0")]
    public void The_rejected_version_or_an_equivalent_spelling_is_blocked(
        FeedbackClassification classification,
        string rejectedVersion)
    {
        UpstreamVerdict verdict = Verdict(classification, rejectedVersion);

        ImmutableArray<ValidationFinding> findings = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.0.0"),
            Manifests("1.0.0"),
            [verdict],
            _noRejectedVersions);

        ValidationFinding finding = Assert.Single(findings);
        Assert.Equal(UpstreamVerdictGate.FindingCode, finding.Code);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Contains("#4711", finding.Message, StringComparison.Ordinal);
        Assert.Contains("--ignore-upstream-verdict", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scanner_and_availability_verdicts_do_not_block_a_new_version()
    {
        ImmutableArray<ValidationFinding> findings = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0"),
            [
                Verdict(FeedbackClassification.ScannerBlocked, "1.0.0"),
                Verdict(FeedbackClassification.InstallerUnavailable, "1.0.0"),
            ],
            _noRejectedVersions);

        Assert.Empty(findings);
    }

    [Fact]
    public void Untrusted_certificate_blocks_every_later_version()
    {
        ImmutableArray<ValidationFinding> findings = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0"),
            [Verdict(FeedbackClassification.UntrustedCertificate, "1.0.0")],
            _noRejectedVersions);

        ValidationFinding finding = Assert.Single(findings);
        Assert.Contains("certificate", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Url_verdict_blocks_only_while_a_rejected_url_remains()
    {
        UpstreamVerdict verdict = Verdict(
            FeedbackClassification.UrlValidationError,
            "1.0.0",
            "https://sqrails.example.com");

        ImmutableArray<ValidationFinding> stillDead = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", publisherUrl: "https://sqrails.example.com"),
            [verdict],
            _noRejectedVersions);
        ImmutableArray<ValidationFinding> repaired = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", publisherUrl: "https://example.com"),
            [verdict],
            _noRejectedVersions);

        ValidationFinding finding = Assert.Single(stillDead);
        Assert.Contains("https://sqrails.example.com", finding.Message, StringComparison.Ordinal);
        Assert.Empty(repaired);
    }

    [Fact]
    public void Installation_failure_blocks_unchanged_installer_traits()
    {
        UpstreamVerdict verdict = Verdict(FeedbackClassification.InstallationFailure, "1.0.0");
        var rejected = new Dictionary<string, PackageManifests>(StringComparer.Ordinal)
        {
            ["1.0.0"] = Manifests("1.0.0", silent: "/S"),
        };

        ImmutableArray<ValidationFinding> unchanged = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", silent: "/S"),
            [verdict],
            rejected);
        ImmutableArray<ValidationFinding> changed = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", silent: "/S /ALLUSERS"),
            [verdict],
            rejected);
        ImmutableArray<ValidationFinding> unknown = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", silent: "/S"),
            [verdict],
            _noRejectedVersions);

        Assert.Single(unchanged);
        Assert.Empty(changed);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Installation_failure_uses_recorded_traits_when_the_rejected_version_was_never_merged()
    {
        // DiRoots.ProSheets 2.4.1 failed unattended installation and was closed unmerged; 2.4.2
        // was resubmitted with the same switches because the gate had nothing to compare with.
        UpstreamVerdict verdict = Verdict(FeedbackClassification.InstallationFailure, "1.0.0") with
        {
            InstallerTraits = UpstreamVerdictGate.InstallerTraits(Manifests("1.0.0", silent: "/S").Installer),
        };

        ImmutableArray<ValidationFinding> unchanged = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", silent: "/S"),
            [verdict],
            _noRejectedVersions);
        ImmutableArray<ValidationFinding> changed = UpstreamVerdictGate.Evaluate(
            _identifier,
            new PackageVersion("1.1.0"),
            Manifests("1.1.0", silent: "/S /ALLUSERS"),
            [verdict],
            _noRejectedVersions);

        Assert.Single(unchanged);
        Assert.Empty(changed);
    }

    private static UpstreamVerdict Verdict(
        FeedbackClassification classification,
        string version,
        params string[] evidence)
        => new(
            4711,
            classification,
            new PackageVersion(version),
            [.. evidence],
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private static PackageManifests Manifests(
        string version,
        string? silent = "/S",
        string publisherUrl = "https://example.com")
    {
        var packageVersion = new PackageVersion(version);
        var locale = new LanguageTag("en-US");
        return new PackageManifests
        {
            Version = new VersionManifest
            {
                PackageIdentifier = _identifier,
                PackageVersion = packageVersion,
                DefaultLocale = locale,
            },
            Installer = new InstallerManifest
            {
                PackageIdentifier = _identifier,
                PackageVersion = packageVersion,
                InstallerType = InstallerType.Nullsoft,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = $"https://example.com/app-{version}.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                        InstallerSwitches = silent is null ? null : new InstallerSwitches { Silent = silent },
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = _identifier,
                PackageVersion = packageVersion,
                PackageLocale = locale,
                Publisher = "Example",
                PublisherUrl = publisherUrl,
                PackageName = "Example App",
                License = "MIT",
                ShortDescription = "Example application",
            },
            Locales = [],
        };
    }
}
