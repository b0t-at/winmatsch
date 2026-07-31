using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class RuleRuntimeTests
{
    [Fact]
    public void Mode_precedence_is_command_then_package_then_user_then_default()
    {
        static ManifestContext Context()
        {
            Installer installer = TestManifests.CreateInstaller();
            installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
            return TestManifests.CreateContext(TestManifests.Create(installer));
        }

        var packagePack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            RuleModes = ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [KeyValuePair.Create(RuleIds.NormalizeProductCodes, RuleMode.Apply)]),
        };
        var packageSet = new OverridePackSet([packagePack]);

        ManifestContext fromDefault = Context();
        new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(defaultMode: RuleMode.Disabled),
            overridePacks: OverridePackSet.Empty).Run(fromDefault);
        Assert.Equal(RuleModeSource.Default, Assert.Single(fromDefault.Executions).ModeSource);
        Assert.Equal(RuleMode.Disabled, Assert.Single(fromDefault.Executions).Mode);

        ManifestContext fromUser = Context();
        new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(
                userOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
                }),
            OverridePackSet.Empty).Run(fromUser);
        Assert.Equal(RuleModeSource.UserConfig, Assert.Single(fromUser.Executions).ModeSource);
        Assert.Equal(RuleMode.LogOnly, Assert.Single(fromUser.Executions).Mode);

        ManifestContext fromPackage = Context();
        new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(
                userOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.NormalizeProductCodes] = RuleMode.Disabled,
                }),
            packageSet).Run(fromPackage);
        Assert.Equal(RuleModeSource.PackageOverride, Assert.Single(fromPackage.Executions).ModeSource);
        Assert.Equal(RuleMode.Apply, Assert.Single(fromPackage.Executions).Mode);

        ManifestContext fromCommand = Context();
        new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(
                userOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
                },
                commandOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.NormalizeProductCodes] = RuleMode.Disabled,
                }),
            packageSet).Run(fromCommand);
        Assert.Equal(RuleModeSource.CommandOverride, Assert.Single(fromCommand.Executions).ModeSource);
        Assert.Equal(RuleMode.Disabled, Assert.Single(fromCommand.Executions).Mode);
    }

    [Fact]
    public void Runtime_configuration_copies_mutable_input_dictionaries()
    {
        var user = new Dictionary<string, RuleMode>
        {
            [RuleIds.NormalizeProductCodes] = RuleMode.Disabled,
        };
        var configuration = new RuleRuntimeConfiguration(userOverrides: user);
        user[RuleIds.NormalizeProductCodes] = RuleMode.Apply;

        Installer installer = TestManifests.CreateInstaller();
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        new RulePipeline([new NormalizeProductCodesRule()], configuration, OverridePackSet.Empty).Run(context);

        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", installer.ProductCode);
        Assert.Equal(RuleMode.Disabled, Assert.Single(context.Executions).Mode);
    }

    [Fact]
    public void Log_only_proposes_the_exact_applied_change_without_mutating()
    {
        static (ManifestContext Context, Installer Installer) Create()
        {
            Installer installer = TestManifests.CreateInstaller();
            installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
            return (TestManifests.CreateContext(TestManifests.Create(installer)), installer);
        }

        (ManifestContext applied, Installer appliedInstaller) = Create();
        (ManifestContext logged, Installer loggedInstaller) = Create();
        var applyPipeline = new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty);
        var logPipeline = new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(
                commandOverrides: new Dictionary<string, RuleMode>
                {
                    [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
                }),
            OverridePackSet.Empty);

        applyPipeline.Run(applied);
        logPipeline.Run(logged);

        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", appliedInstaller.ProductCode);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", loggedInstaller.ProductCode);
        RuleChange appliedChange = Assert.Single(applied.Changes);
        RuleChange loggedChange = Assert.Single(logged.Changes);
        Assert.Equal(appliedChange.ManifestPath, loggedChange.ManifestPath);
        Assert.Equal(appliedChange.FieldPath, loggedChange.FieldPath);
        Assert.Equal(appliedChange.Before, loggedChange.Before);
        Assert.Equal(appliedChange.After, loggedChange.After);
        Assert.Equal(RuleMode.Apply, appliedChange.Mode);
        Assert.Equal(RuleMode.LogOnly, loggedChange.Mode);
    }

    [Fact]
    public void Structured_logs_redact_uri_credentials_and_query_values()
    {
        const string secretUrl = "https://user:password@example.com/app.exe?token=super-secret";
        Installer installer = TestManifests.CreateInstaller(url: secretUrl);
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));
        var pipeline = new RulePipeline(
            [new ReplaceUrlRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty);

        pipeline.Run(context);

        RuleChange change = Assert.Single(context.Changes);
        Assert.DoesNotContain("password", change.Before, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", change.Before, StringComparison.Ordinal);
        Assert.DoesNotContain("new-secret", change.After, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", change.After, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Findings_and_explain_trace_never_expose_credentials()
    {
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);
        var rule = new CredentialLoggingRule();

        rule.Apply(context);

        RuleFinding finding = Assert.Single(context.Findings);
        RuleTraceEntry trace = Assert.Single(context.Trace);
        Assert.DoesNotContain("password", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", trace.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", trace.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_reversion_of_a_known_human_correction_requires_review()
    {
        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        originalBot.DefaultLocale.PublisherUrl = "https://old.example.test";
        generated.DefaultLocale.PublisherUrl = "https://old.example.test";
        merged.DefaultLocale.PublisherUrl = "https://correct.example.test";
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        new RulePipeline([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
        HumanCorrectionReview review = Assert.Single(context.HumanCorrectionReviews);
        Assert.EndsWith(".locale.en-US.yaml", review.ManifestPath, StringComparison.Ordinal);
        Assert.Equal("PublisherUrl", review.FieldPath);
        Assert.Equal("https://correct.example.test/", review.HumanValue);
    }

    [Fact]
    public void Human_correction_detection_matches_documents_when_identifier_was_corrected()
    {
        static void SetIdentifier(PackageManifests manifests, string value)
        {
            var identifier = new PackageIdentifier(value);
            manifests.Installer.PackageIdentifier = identifier;
            manifests.DefaultLocale.PackageIdentifier = identifier;
            manifests.Version.PackageIdentifier = identifier;
        }

        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        SetIdentifier(originalBot, "Old.App");
        SetIdentifier(generated, "Old.App");
        SetIdentifier(merged, "Correct.App");
        originalBot.DefaultLocale.PublisherUrl = "https://old.example.test";
        generated.DefaultLocale.PublisherUrl = "https://old.example.test";
        merged.DefaultLocale.PublisherUrl = "https://correct.example.test";
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        new RulePipeline([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath == "PublisherUrl"
                && review.HumanValue == "https://correct.example.test/");
    }

    [Fact]
    public void Validation_rules_still_process_incomplete_manifests()
    {
        Installer first = TestManifests.CreateInstaller();
        Installer second = TestManifests.CreateInstaller();
        first.InstallerSha256 = null;
        second.InstallerSha256 = null;
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(first, second));

        new RulePipeline(
            [new DuplicateInstallerEntriesRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Contains(context.Findings, finding => finding.RuleId == RuleIds.DuplicateInstallerEntries);
    }

    [Fact]
    public void Applied_mutating_rules_preserve_incomplete_manifest_compatibility()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSha256 = null;
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        new RulePipeline(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", installer.ProductCode);
        Assert.Single(context.Changes);
    }

    [Fact]
    public void Log_only_is_exact_for_manifests_with_a_missing_installer_hash()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSha256 = null;
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));
        var configuration = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
            });

        new RulePipeline(
            [new NormalizeProductCodesRule()],
            configuration,
            OverridePackSet.Empty).Run(context);

        Assert.Null(installer.InstallerSha256);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", installer.ProductCode);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", change.Before);
        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", change.After);
    }

    [Fact]
    public void Catalogue_ids_keep_the_exact_policy_names()
    {
        Assert.Equal("ARCH-1", RuleCatalogueIds.Arch1);
        Assert.Equal("MAP-2", RuleCatalogueIds.Map2);
        Assert.Equal("PIPE-5", RuleCatalogueIds.Pipe5);
        Assert.Equal("WM0201", RuleIds.ApplyPackageQuirks);
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Cookie: session=top-secret")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwxyz")]
    public void Common_authorization_material_is_redacted(string message)
    {
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);
        var rule = new DirectLoggingRule(message);

        rule.Apply(context);

        Assert.Equal("[REDACTED]", Assert.Single(context.Trace).Message);
    }

    private sealed class ReplaceUrlRule : IRule
    {
        public string Id => "WM9998";

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Info;

        public string Description => "Test URL replacement.";

        public void Apply(ManifestContext context)
        {
            context.Manifests.Installer.Installers![0].InstallerUrl =
                "https://other:credential@example.com/new.exe?token=new-secret";
        }
    }

    private sealed class CredentialLoggingRule : IRule
    {
        public string Id => "WM9997";

        public RuleCategory Category => RuleCategory.Validation;

        public RuleSeverity Severity => RuleSeverity.Warning;

        public string Description => "Test credential-safe logging.";

        public void Apply(ManifestContext context)
        {
            context.AddFinding(
                this,
                "Rejected https://user:password@example.com/app.exe?token=top-secret");
        }
    }

    private sealed class DirectLoggingRule(string message) : IRule
    {
        public string Id => "WM9996";

        public RuleCategory Category => RuleCategory.Validation;

        public RuleSeverity Severity => RuleSeverity.Warning;

        public string Description => "Test direct logging.";

        public void Apply(ManifestContext context) => context.AddTrace(this, message);
    }
}
