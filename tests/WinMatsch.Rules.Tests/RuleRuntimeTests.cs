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
        RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(defaultMode: RuleMode.Disabled),
            overridePacks: OverridePackSet.Empty).Run(fromDefault);
        Assert.Equal(RuleModeSource.Default, Assert.Single(fromDefault.Executions).ModeSource);
        Assert.Equal(RuleMode.Disabled, Assert.Single(fromDefault.Executions).Mode);

        ManifestContext fromUser = Context();
        RulePipeline.Create(
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
        RulePipeline.Create(
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
        RulePipeline.Create(
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

        RulePipeline.Create([new NormalizeProductCodesRule()], configuration, OverridePackSet.Empty).Run(context);

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
        RulePipeline applyPipeline = RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty);
        RulePipeline logPipeline = RulePipeline.Create(
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
        RulePipeline pipeline = RulePipeline.Create(
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

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

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

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath == "PublisherUrl"
                && review.HumanValue == "https://correct.example.test/");
    }

    [Fact]
    public void Human_correction_detection_matches_locales_by_package_locale_after_reorder()
    {
        static LocaleManifest Locale(string language, string description) => new()
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            PackageVersion = new PackageVersion(TestManifests.DefaultVersion),
            PackageLocale = new LanguageTag(language),
            Publisher = TestManifests.DefaultPublisher,
            PackageName = TestManifests.DefaultPackageName,
            License = "MIT",
            ShortDescription = description,
        };

        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        originalBot.Locales = [Locale("de-DE", "Deutsch"), Locale("fr-FR", "Old French")];
        merged.Locales = [Locale("fr-FR", "Correct French"), Locale("de-DE", "Deutsch")];
        generated.Locales = [Locale("de-DE", "Deutsch"), Locale("fr-FR", "Old French")];
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.ManifestPath.EndsWith(".locale.fr-FR.yaml", StringComparison.Ordinal)
                && review.FieldPath == "ShortDescription"
                && review.HumanValue == "Correct French");
    }

    [Fact]
    public void Human_correction_detection_matches_installers_after_reorder()
    {
        static Installer Installer(Architecture architecture, string url, string productCode)
        {
            Installer installer = TestManifests.CreateInstaller(architecture, url: url);
            installer.ProductCode = productCode;
            return installer;
        }

        PackageManifests originalBot = TestManifests.Create(
            Installer(Architecture.X64, "https://example.test/app-x64-1.0.exe", "BOT-A"),
            Installer(Architecture.X86, "https://example.test/app-x86-1.0.exe", "BOT-B"));
        PackageManifests merged = TestManifests.Create(
            Installer(Architecture.X86, "https://example.test/app-x86-1.0.exe", "HUMAN-B"),
            Installer(Architecture.X64, "https://example.test/app-x64-1.0.exe", "BOT-A"));
        PackageManifests generated = TestManifests.Create(
            Installer(Architecture.X64, "https://example.test/app-x64-2.0.exe", "BOT-A"),
            Installer(Architecture.X86, "https://example.test/app-x86-2.0.exe", "BOT-B"));
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.HumanValue == "HUMAN-B");
    }

    [Fact]
    public void Installer_matching_handles_version_shape_changes_and_effective_root_fields()
    {
        static PackageManifests Create(string url, string productCode)
        {
            Installer installer = TestManifests.CreateInstaller(url: url);
            installer.InstallerType = null;
            installer.Scope = null;
            installer.ProductCode = productCode;
            PackageManifests manifests = TestManifests.Create(installer);
            manifests.Installer.InstallerType = InstallerType.Msi;
            manifests.Installer.Scope = Scope.Machine;
            return manifests;
        }

        PackageManifests originalBot = Create(
            "https://example.test/app-x64-1.2.3.exe",
            "BOT-CODE");
        PackageManifests merged = Create(
            "https://example.test/app-x64-1.2.3.exe",
            "HUMAN-CODE");
        PackageManifests generated = Create(
            "https://example.test/app-x64-2.0.exe",
            "BOT-CODE");
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.HumanValue == "HUMAN-CODE");
    }

    [Fact]
    public void Renamed_installer_url_does_not_hide_another_reverted_field()
    {
        static PackageManifests Create(string url, string productCode)
        {
            Installer installer = TestManifests.CreateInstaller(url: url);
            installer.ProductCode = productCode;
            return TestManifests.Create(installer);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("https://example.test/old-app.exe", "A"),
            Previous = Create("https://example.test/old-app.exe", "B"),
            Manifests = Create("https://example.test/renamed-app.exe", "A"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.HumanValue == "B"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Root_identity_change_keeps_installer_pairing_for_other_corrections()
    {
        static PackageManifests Create(InstallerType rootType, string productCode)
        {
            Installer installer = TestManifests.CreateInstaller(
                installerType: null,
                url: "https://example.test/app.exe");
            installer.ProductCode = productCode;
            PackageManifests manifests = TestManifests.Create(installer);
            manifests.Installer.InstallerType = rootType;
            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(InstallerType.Msi, "A"),
            Previous = Create(InstallerType.Msi, "B"),
            Manifests = Create(InstallerType.Inno, "A"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.HumanValue == "B"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Versioned_url_pattern_reversion_requires_review()
    {
        static PackageManifests Create(string url)
            => TestManifests.Create(TestManifests.CreateInstaller(url: url));

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("https://example.test/app-win64-1.0.exe"),
            Previous = Create("https://example.test/app-win32-1.0.exe"),
            Manifests = Create("https://example.test/app-win64-2.0.exe"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".InstallerUrl", StringComparison.Ordinal)
                && review.HumanValue == "https://example.test/app-win32-1.0.exe");
    }

    [Fact]
    public void Inherited_bot_value_is_used_when_a_human_added_an_installer_override()
    {
        static PackageManifests Create(string rootProductCode, string? firstOverride)
        {
            Installer first = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            first.ProductCode = firstOverride;
            Installer second = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            PackageManifests manifests = TestManifests.Create(first, second);
            manifests.Installer.ProductCode = rootProductCode;
            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("A", firstOverride: null),
            Previous = Create("A", firstOverride: "B"),
            Manifests = Create("A", firstOverride: null),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.BotValue == "A"
                && review.HumanValue == "B"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Root_correction_reversion_is_detected_after_generated_pushdown()
    {
        static PackageManifests Create(string? rootProductCode, string? installerProductCode)
        {
            Installer first = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            first.ProductCode = installerProductCode;
            Installer second = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            second.ProductCode = installerProductCode;
            PackageManifests manifests = TestManifests.Create(first, second);
            manifests.Installer.ProductCode = rootProductCode;
            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("A", installerProductCode: null),
            Previous = Create("B", installerProductCode: null),
            Manifests = Create(rootProductCode: null, "A"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath == "ProductCode"
                && review.BotValue == "A"
                && review.HumanValue == "B"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Any_generated_installer_restoring_a_root_bot_value_requires_review()
    {
        static PackageManifests Create(string? rootProductCode, string first, string second)
        {
            Installer firstInstaller = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            firstInstaller.ProductCode = first;
            Installer secondInstaller = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            secondInstaller.ProductCode = second;
            PackageManifests manifests = TestManifests.Create(firstInstaller, secondInstaller);
            manifests.Installer.ProductCode = rootProductCode;
            if (rootProductCode is not null)
            {
                firstInstaller.ProductCode = null;
                secondInstaller.ProductCode = null;
            }

            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("A", "A", "A"),
            Previous = Create("B", "B", "B"),
            Manifests = Create(rootProductCode: null, "A", "C"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath == "ProductCode"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Mixed_generated_values_without_the_bot_value_do_not_trigger_review()
    {
        static PackageManifests Create(string? rootProductCode, string first, string second)
        {
            Installer firstInstaller = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            firstInstaller.ProductCode = first;
            Installer secondInstaller = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            secondInstaller.ProductCode = second;
            PackageManifests manifests = TestManifests.Create(firstInstaller, secondInstaller);
            manifests.Installer.ProductCode = rootProductCode;
            if (rootProductCode is not null)
            {
                firstInstaller.ProductCode = null;
                secondInstaller.ProductCode = null;
            }

            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create("A", "A", "A"),
            Previous = Create("B", "B", "B"),
            Manifests = Create(rootProductCode: null, "C", "D"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Missing_bot_value_restored_by_one_mixed_installer_requires_review()
    {
        static PackageManifests Create(string? rootProductCode, string? first, string? second)
        {
            Installer firstInstaller = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            firstInstaller.ProductCode = first;
            Installer secondInstaller = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            secondInstaller.ProductCode = second;
            PackageManifests manifests = TestManifests.Create(firstInstaller, secondInstaller);
            manifests.Installer.ProductCode = rootProductCode;
            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(rootProductCode: null, first: null, second: null),
            Previous = Create("B", first: null, second: null),
            Manifests = Create(rootProductCode: null, "C", second: null),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
    }

    [Fact]
    public void Missing_bot_value_with_mixed_generated_values_does_not_trigger_review()
    {
        static PackageManifests Create(string? rootProductCode, string? first, string? second)
        {
            Installer firstInstaller = TestManifests.CreateInstaller(url: "https://example.test/a.exe");
            firstInstaller.ProductCode = first;
            Installer secondInstaller = TestManifests.CreateInstaller(url: "https://example.test/b.exe");
            secondInstaller.ProductCode = second;
            PackageManifests manifests = TestManifests.Create(firstInstaller, secondInstaller);
            manifests.Installer.ProductCode = rootProductCode;
            return manifests;
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(rootProductCode: null, first: null, second: null),
            Previous = Create("B", first: null, second: null),
            Manifests = Create(rootProductCode: null, "C", "D"),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Hoisted_generated_value_cannot_hide_a_reverted_installer_correction()
    {
        static Installer Installer(string url, string productCode)
        {
            Installer installer = TestManifests.CreateInstaller(url: url);
            installer.ProductCode = productCode;
            return installer;
        }

        PackageManifests originalBot = TestManifests.Create(
            Installer("https://example.test/a.exe", "A"),
            Installer("https://example.test/c.exe", "C"));
        PackageManifests merged = TestManifests.Create(
            Installer("https://example.test/a.exe", "B"),
            Installer("https://example.test/c.exe", "C"));
        PackageManifests generated = TestManifests.Create(
            Installer("https://example.test/a.exe", "A"),
            Installer("https://example.test/c.exe", "A"));
        var context = new ManifestContext
        {
            OriginalBotSubmission = originalBot,
            Previous = merged,
            Manifests = generated,
        };

        RulePipeline.Create(
            [new HoistCommonInstallerFieldsRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Equal("A", generated.Installer.ProductCode);
        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal)
                && review.HumanValue == "B"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Hoisted_nested_value_cannot_hide_a_reverted_installer_correction()
    {
        static Installer Installer(string url, string silent)
        {
            Installer installer = TestManifests.CreateInstaller(url: url);
            installer.InstallerSwitches = new() { Silent = silent };
            return installer;
        }

        PackageManifests originalBot = TestManifests.Create(
            Installer("https://example.test/a.exe", "/old"),
            Installer("https://example.test/c.exe", "/other"));
        PackageManifests merged = TestManifests.Create(
            Installer("https://example.test/a.exe", "/human"),
            Installer("https://example.test/c.exe", "/other"));
        PackageManifests generated = TestManifests.Create(
            Installer("https://example.test/a.exe", "/old"),
            Installer("https://example.test/c.exe", "/old"));
        var context = new ManifestContext
        {
            OriginalBotSubmission = originalBot,
            Previous = merged,
            Manifests = generated,
        };

        RulePipeline.Create(
            [new HoistCommonInstallerFieldsRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Equal("/old", generated.Installer.InstallerSwitches?.Silent);
        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.EndsWith(".InstallerSwitches.Silent", StringComparison.Ordinal)
                && review.HumanValue == "[REDACTED]"
                && review.GeneratedValue == "[REDACTED]");
    }

    [Fact]
    public void Human_inserted_installer_is_not_hidden_by_index_shifts()
    {
        Installer original = TestManifests.CreateInstaller(url: "https://example.test/app-x64.exe");
        Installer inserted = TestManifests.CreateInstaller(
            Architecture.Arm64,
            url: "https://example.test/app-arm64.exe");
        PackageManifests originalBot = TestManifests.Create(original);
        PackageManifests merged = TestManifests.Create(inserted, TestManifests.CreateInstaller(url: original.InstallerUrl));
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller(url: original.InstallerUrl));
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.StartsWith("Installers[0].", StringComparison.Ordinal)
                && review.HumanValue == "arm64");
    }

    [Fact]
    public void Human_removed_installer_is_not_hidden_by_index_shifts()
    {
        PackageManifests originalBot = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.test/app-x64.exe"),
            TestManifests.CreateInstaller(Architecture.X86, url: "https://example.test/app-x86.exe"));
        PackageManifests merged = TestManifests.Create(
            TestManifests.CreateInstaller(Architecture.X86, url: "https://example.test/app-x86.exe"));
        PackageManifests generated = TestManifests.Create(
            TestManifests.CreateInstaller(url: "https://example.test/app-x64.exe"),
            TestManifests.CreateInstaller(Architecture.X86, url: "https://example.test/app-x86.exe"));
        var context = new ManifestContext
        {
            Manifests = generated,
            Previous = merged,
            OriginalBotSubmission = originalBot,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath.StartsWith("Installers[0].", StringComparison.Ordinal)
                && review.BotValue == "x64"
                && review.HumanValue is null);
    }

    [Fact]
    public void Duplicate_sequence_removal_logs_only_the_removed_occurrence()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.Commands = ["A", "A", "B"];
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        RulePipeline.Create(
            [new ReplaceCommandsRule(["A", "B"])],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal("Installers[0].Commands[1]", change.FieldPath);
        Assert.Equal("A", change.Before);
        Assert.Null(change.After);
    }

    [Fact]
    public void Duplicate_sequence_change_logs_the_changed_occurrence_without_shifting_following_values()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.Commands = ["A", "A", "B"];
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        RulePipeline.Create(
            [new ReplaceCommandsRule(["A", "C", "B"])],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal("Installers[0].Commands[1]", change.FieldPath);
        Assert.Equal("A", change.Before);
        Assert.Equal("C", change.After);
    }

    [Fact]
    public void Large_sequence_diff_uses_bounded_memory_and_reports_the_single_removal()
    {
        List<string> before = [.. Enumerable.Range(0, 2048).Select(static index => $"Command{index}")];
        List<string> after = [.. before];
        after.RemoveAt(1024);
        Installer installer = TestManifests.CreateInstaller();
        installer.Commands = before;
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        RulePipeline.Create(
            [new ReplaceCommandsRule(after)],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal("Installers[0].Commands[1024]", change.FieldPath);
        Assert.Equal("Command1024", change.Before);
        Assert.Null(change.After);
    }

    [Fact]
    public void Validation_rules_still_process_incomplete_manifests()
    {
        Installer first = TestManifests.CreateInstaller();
        Installer second = TestManifests.CreateInstaller();
        first.InstallerSha256 = null;
        second.InstallerSha256 = null;
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(first, second));

        RulePipeline.Create(
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

        RulePipeline.Create(
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

        RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            configuration,
            OverridePackSet.Empty).Run(context);

        Assert.Null(installer.InstallerSha256);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", installer.ProductCode);
        RuleChange change = Assert.Single(context.Changes);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", change.Before);
        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", change.After);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Log_only_runs_with_reader_accepted_invalid_market_shapes(bool bothLists)
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        installer.Markets = bothLists
            ? new Markets { AllowedMarkets = ["US"], ExcludedMarkets = ["FR"] }
            : new Markets();
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));
        var configuration = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
            });

        RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            configuration,
            OverridePackSet.Empty).Run(context);

        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", installer.ProductCode);
        Assert.Contains(
            context.Changes,
            change => change.After == "{AB12CD34-EF56-7890-ABCD-EF1234567890}");
    }

    [Fact]
    public void Log_only_runs_when_unrelated_required_manifest_fields_are_missing()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        PackageManifests manifests = TestManifests.Create(installer);
        manifests.Version.DefaultLocale = null;
        ManifestContext context = TestManifests.CreateContext(manifests);
        var configuration = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.NormalizeProductCodes] = RuleMode.LogOnly,
            });

        RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            configuration,
            OverridePackSet.Empty).Run(context);

        Assert.Null(manifests.Version.DefaultLocale);
        Assert.Equal("ab12cd34-ef56-7890-abcd-ef1234567890", installer.ProductCode);
        RuleChange change = Assert.Single(
            context.Changes,
            item => item.FieldPath.EndsWith(".ProductCode", StringComparison.Ordinal));
        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", change.After);
    }

    [Fact]
    public void Placeholder_fallback_records_required_fields_changed_to_null_exactly()
    {
        Installer installer = TestManifests.CreateInstaller(url: "   ");
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));
        var configuration = new RuleRuntimeConfiguration(
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.ScrubEmptyStrings] = RuleMode.LogOnly,
            });

        RulePipeline.Create(
            [new ScrubEmptyStringsRule()],
            configuration,
            OverridePackSet.Empty).Run(context);

        Assert.Equal("   ", installer.InstallerUrl);
        RuleChange change = Assert.Single(
            context.Changes,
            item => item.FieldPath.EndsWith(".InstallerUrl", StringComparison.Ordinal));
        Assert.Equal("   ", change.Before);
        Assert.Null(change.After);
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
    [InlineData("Authorization: Bearer abc.def~ghi+jkl/mno")]
    [InlineData("Received bearer abcDEF123,")]
    [InlineData("Received `bearer abcDEF123`")]
    [InlineData("Received <bearer abcDEF123>")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzKysv")]
    [InlineData("Authorization: Basic YTpi")]
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

    [Fact]
    public void Embedded_urls_are_sanitized_without_discarding_surrounding_text()
    {
        const string message =
            "Evidence from https://user:password@example.test/app.exe?sig=do-not-log#fragment was accepted.";
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);

        new DirectLoggingRule(message).Apply(context);

        string sanitized = Assert.Single(context.Trace).Message;
        Assert.Equal("Evidence from https://example.test/app.exe was accepted.", sanitized);
        Assert.DoesNotContain("password", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-log", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("basic installer session")]
    [InlineData("Token Studio")]
    [InlineData("secret sauce")]
    public void Harmless_credential_words_are_not_redacted(string message)
    {
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);

        new DirectLoggingRule(message).Apply(context);

        Assert.Equal(message, Assert.Single(context.Trace).Message);
    }

    [Theory]
    [InlineData("--password hunter2")]
    [InlineData("/password:hunter2")]
    [InlineData("session=top-secret")]
    [InlineData("access_token=oauth-secret")]
    [InlineData("refreshToken: oauth-secret")]
    [InlineData("{\"access_token\":\"oauth-secret\"}")]
    [InlineData("oauth_token=oauth-secret")]
    [InlineData("{\"client_secret\":\"oauth-secret\"}")]
    [InlineData("--access-token oauth-secret")]
    [InlineData("--oauth-token oauth-secret")]
    [InlineData("oauth_client_secret=oauth-secret")]
    [InlineData("{\"oauthClientSecret\":\"oauth-secret\"}")]
    public void Syntactically_bounded_credential_markers_are_redacted(string message)
    {
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);

        new DirectLoggingRule(message).Apply(context);

        Assert.Equal("[REDACTED]", Assert.Single(context.Trace).Message);
    }

    [Fact]
    public void Credential_assignments_in_url_paths_are_redacted()
    {
        const string message = "https://example.test/download/token=super-secret/app.exe";
        ManifestContext context = TestManifests.CreateContext(
            TestManifests.Create(TestManifests.CreateInstaller()),
            explain: true);

        new DirectLoggingRule(message).Apply(context);

        Assert.Equal("[REDACTED]", Assert.Single(context.Trace).Message);
    }

    [Theory]
    [InlineData("Download from https://example.test/file?sig=super-secret")]
    [InlineData("Download from https://example.test/token%3Dsuper-secret/file.exe")]
    [InlineData("Download from https://example.test/token%253Dsuper-secret/file.exe")]
    [InlineData("Download from https://example.test/file?x=1;sig=super-secret")]
    [InlineData("Download from https://example.test:99999/file?sig=super-secret")]
    [InlineData("https%3A%2F%2Fexample.test%2Ffile%3Fsig%3Dsuper-secret")]
    public void Embedded_signed_or_encoded_urls_cannot_leak_from_structured_values(string value)
    {
        string? sanitized = RuleLogSanitizer.Sanitize("SourceEvidence", value);

        Assert.DoesNotContain("super-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("?sig=", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_http_uri_userinfo_and_query_are_sanitized()
    {
        const string value = "Downloaded from ftp://user:password@example.test/app.exe?token=secret";

        string sanitized = RuleLogSanitizer.SanitizeMessage(value);

        Assert.DoesNotContain("password", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_http_uri_query_is_removed_before_scheme_specific_parsing()
    {
        const string value = "ftp://example.test/app.exe?sig=do-not-log";

        string sanitized = RuleLogSanitizer.SanitizeMessage(value);

        Assert.DoesNotContain("do-not-log", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_url_multi_arch_installers_match_by_architecture_after_reorder()
    {
        static PackageManifests Create(bool reversed)
        {
            Installer x86 = TestManifests.CreateInstaller(
                Architecture.X86,
                url: "https://example.test/universal.zip");
            Installer x64 = TestManifests.CreateInstaller(
                Architecture.X64,
                url: "https://example.test/universal.zip");
            return reversed ? TestManifests.Create(x64, x86) : TestManifests.Create(x86, x64);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(reversed: false),
            Previous = Create(reversed: true),
            Manifests = Create(reversed: false),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Sentence_punctuation_does_not_bypass_jwt_redaction()
    {
        const string message =
            "Token: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwxyz.";

        string sanitized = RuleLogSanitizer.SanitizeMessage(message);

        Assert.Equal("[REDACTED]", sanitized);
    }

    [Fact]
    public void Backtick_wrapping_does_not_bypass_jwt_redaction()
    {
        const string message =
            "Received `eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwxyz`";

        Assert.Equal("[REDACTED]", RuleLogSanitizer.SanitizeMessage(message));
    }

    [Fact]
    public void Missing_hash_changes_are_audited_from_null_not_a_serialization_placeholder()
    {
        Installer installer = TestManifests.CreateInstaller();
        installer.InstallerSha256 = null;
        ManifestContext context = TestManifests.CreateContext(TestManifests.Create(installer));

        RulePipeline.Create(
            [new SetHashRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        RuleChange change = Assert.Single(
            context.Changes,
            item => item.FieldPath.EndsWith(".InstallerSha256", StringComparison.Ordinal));
        Assert.Null(change.Before);
        Assert.Equal(new string('A', Sha256Hash.Length), change.After);
    }

    [Fact]
    public void Numeric_architecture_urls_remain_distinct_when_installers_reorder()
    {
        static PackageManifests Create(bool reversed)
        {
            Installer win32 = TestManifests.CreateInstaller(
                Architecture.X86,
                url: "https://example.test/app-win32-1.0.exe");
            Installer win64 = TestManifests.CreateInstaller(
                Architecture.X64,
                url: "https://example.test/app-win64-1.0.exe");
            return reversed
                ? TestManifests.Create(win64, win32)
                : TestManifests.Create(win32, win64);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(reversed: false),
            Previous = Create(reversed: true),
            Manifests = Create(reversed: false),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
        Assert.Empty(context.HumanCorrectionReviews);
    }

    [Fact]
    public void Plain_bit_architecture_urls_remain_distinct_when_installers_reorder()
    {
        static PackageManifests Create(bool reversed)
        {
            Installer bit32 = TestManifests.CreateInstaller(
                Architecture.X86,
                url: "https://example.test/app-32bit.exe");
            Installer bit64 = TestManifests.CreateInstaller(
                Architecture.X64,
                url: "https://example.test/app-64bit.exe");
            return reversed
                ? TestManifests.Create(bit64, bit32)
                : TestManifests.Create(bit32, bit64);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(reversed: false),
            Previous = Create(reversed: true),
            Manifests = Create(reversed: false),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Armv_architecture_urls_remain_distinct_when_installers_reorder()
    {
        static PackageManifests Create(bool reversed)
        {
            Installer armv7 = TestManifests.CreateInstaller(
                Architecture.Arm,
                url: "https://example.test/app-armv7-1.0.exe");
            Installer armv8 = TestManifests.CreateInstaller(
                Architecture.Arm64,
                url: "https://example.test/app-armv8-1.0.exe");
            return reversed
                ? TestManifests.Create(armv8, armv7)
                : TestManifests.Create(armv7, armv8);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(reversed: false),
            Previous = Create(reversed: true),
            Manifests = Create(reversed: false),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Aarch_architecture_urls_remain_distinct_when_installers_reorder()
    {
        static PackageManifests Create(bool reversed)
        {
            Installer aarch32 = TestManifests.CreateInstaller(
                Architecture.Arm,
                url: "https://example.test/app-aarch32-1.0.exe");
            Installer aarch64 = TestManifests.CreateInstaller(
                Architecture.Arm64,
                url: "https://example.test/app-aarch64-1.0.exe");
            return reversed
                ? TestManifests.Create(aarch64, aarch32)
                : TestManifests.Create(aarch32, aarch64);
        }

        var context = new ManifestContext
        {
            OriginalBotSubmission = Create(reversed: false),
            Previous = Create(reversed: true),
            Manifests = Create(reversed: false),
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Duplicate_locale_values_do_not_abort_mutating_rules()
    {
        static LocaleManifest Locale() => new()
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            PackageVersion = new PackageVersion(TestManifests.DefaultVersion),
            PackageLocale = new LanguageTag("fr-FR"),
            Publisher = TestManifests.DefaultPublisher,
            PackageName = TestManifests.DefaultPackageName,
            License = "MIT",
            ShortDescription = "French",
        };

        Installer installer = TestManifests.CreateInstaller();
        installer.ProductCode = "ab12cd34-ef56-7890-abcd-ef1234567890";
        PackageManifests manifests = TestManifests.Create(installer);
        manifests.Locales = [Locale(), Locale()];
        ManifestContext context = TestManifests.CreateContext(manifests);

        RulePipeline.Create(
            [new NormalizeProductCodesRule()],
            new RuleRuntimeConfiguration(),
            OverridePackSet.Empty).Run(context);

        Assert.Equal("{AB12CD34-EF56-7890-ABCD-EF1234567890}", installer.ProductCode);
    }

    [Fact]
    public void Duplicate_locale_reordering_does_not_hide_a_reverted_correction()
    {
        static LocaleManifest Locale(string description) => new()
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            PackageVersion = new PackageVersion(TestManifests.DefaultVersion),
            PackageLocale = new LanguageTag("fr-FR"),
            Publisher = TestManifests.DefaultPublisher,
            PackageName = TestManifests.DefaultPackageName,
            License = "MIT",
            ShortDescription = description,
        };

        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        originalBot.Locales = [Locale("A"), Locale("B")];
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        merged.Locales = [Locale("B"), Locale("A-corrected")];
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        generated.Locales = [Locale("B"), Locale("A")];
        var context = new ManifestContext
        {
            OriginalBotSubmission = originalBot,
            Previous = merged,
            Manifests = generated,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.Contains(
            context.HumanCorrectionReviews,
            review => review.FieldPath == "ShortDescription"
                && review.HumanValue == "A-corrected"
                && review.GeneratedValue == "A");
    }

    [Fact]
    public void Identical_added_duplicate_locales_have_unique_review_keys()
    {
        static LocaleManifest Locale() => new()
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            PackageVersion = new PackageVersion(TestManifests.DefaultVersion),
            PackageLocale = new LanguageTag("fr-FR"),
            Publisher = TestManifests.DefaultPublisher,
            PackageName = TestManifests.DefaultPackageName,
            License = "MIT",
            ShortDescription = "French",
        };

        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        merged.Locales = [Locale(), Locale()];
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        var context = new ManifestContext
        {
            OriginalBotSubmission = originalBot,
            Previous = merged,
            Manifests = generated,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
        Assert.NotEmpty(context.HumanCorrectionReviews);
    }

    [Fact]
    public void Duplicate_locale_similarity_matching_has_a_bounded_fallback()
    {
        static LocaleManifest Locale(int index, string prefix) => new()
        {
            PackageIdentifier = new PackageIdentifier("Test.App"),
            PackageVersion = new PackageVersion(TestManifests.DefaultVersion),
            PackageLocale = new LanguageTag("fr-FR"),
            Publisher = TestManifests.DefaultPublisher,
            PackageName = TestManifests.DefaultPackageName,
            License = "MIT",
            ShortDescription = $"{prefix}{index}",
        };

        const int localeCount = 1001;
        PackageManifests originalBot = TestManifests.Create(TestManifests.CreateInstaller());
        originalBot.Locales =
        [
            .. Enumerable.Range(0, localeCount).Select(index => Locale(index, "A")),
        ];
        PackageManifests merged = TestManifests.Create(TestManifests.CreateInstaller());
        merged.Locales =
        [
            .. Enumerable.Range(0, localeCount).Select(index => Locale(index, "B")),
        ];
        PackageManifests generated = TestManifests.Create(TestManifests.CreateInstaller());
        generated.Locales =
        [
            .. Enumerable.Range(0, localeCount).Select(index => Locale(index, "A")),
        ];
        var context = new ManifestContext
        {
            OriginalBotSubmission = originalBot,
            Previous = merged,
            Manifests = generated,
        };

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.True(context.RequiresReview);
    }

    [Fact]
    public void Review_gating_collections_cannot_be_cast_back_to_mutable_lists()
    {
        var context = new ManifestContext
        {
            Manifests = TestManifests.Create(TestManifests.CreateInstaller()),
        };

        Assert.False(context.Changes is List<RuleChange>);
        Assert.False(context.Executions is List<RuleExecution>);
        Assert.False(context.HumanCorrectionReviews is List<HumanCorrectionReview>);
        Assert.True(((IList<RuleChange>)context.Changes).IsReadOnly);
        Assert.True(((IList<HumanCorrectionReview>)context.HumanCorrectionReviews).IsReadOnly);
    }

    [Fact]
    public void Sequence_reordering_does_not_create_duplicate_semantic_change_keys()
    {
        Installer originalInstaller = TestManifests.CreateInstaller();
        originalInstaller.Commands = ["A", "B"];
        Installer generatedInstaller = TestManifests.CreateInstaller();
        generatedInstaller.Commands = ["B", "A"];
        var context = new ManifestContext
        {
            OriginalBotSubmission = TestManifests.Create(originalInstaller),
            Previous = TestManifests.Create(TestManifests.CreateInstaller()),
            Manifests = TestManifests.Create(generatedInstaller),
        };
        context.Previous.Installer.Installers![0].Commands = ["A", "B"];

        RulePipeline.Create([], new RuleRuntimeConfiguration(), OverridePackSet.Empty).Run(context);

        Assert.False(context.RequiresReview);
    }

    [Fact]
    public void Disabled_rule_ids_only_reports_unconditional_command_disables()
    {
        var configuration = new RuleRuntimeConfiguration(
            userOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.NormalizeProductCodes] = RuleMode.Disabled,
            },
            commandOverrides: new Dictionary<string, RuleMode>
            {
                [RuleIds.NormalizeProductCodes] = RuleMode.Apply,
                [RuleIds.ScrubEmptyStrings] = RuleMode.Disabled,
            });
        RulePipeline pipeline = RulePipeline.Create(
            [new NormalizeProductCodesRule(), new ScrubEmptyStringsRule()],
            configuration,
            OverridePackSet.Empty);

        Assert.DoesNotContain(RuleIds.NormalizeProductCodes, pipeline.DisabledRuleIds);
        Assert.Contains(RuleIds.ScrubEmptyStrings, pipeline.DisabledRuleIds);
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

    private sealed class ReplaceCommandsRule(List<string> commands) : IRule
    {
        public string Id => "WM9995";

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Info;

        public string Description => "Test semantic sequence logging.";

        public void Apply(ManifestContext context)
        {
            context.Manifests.Installer.Installers![0].Commands = [.. commands];
        }
    }

    private sealed class SetHashRule : IRule
    {
        public string Id => "WM9994";

        public RuleCategory Category => RuleCategory.Normalization;

        public RuleSeverity Severity => RuleSeverity.Info;

        public string Description => "Test exact missing hash logging.";

        public void Apply(ManifestContext context)
        {
            context.Manifests.Installer.Installers![0].InstallerSha256 =
                new Sha256Hash(new string('A', Sha256Hash.Length));
        }
    }
}
