using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using Xunit;

namespace WinMatsch.Rules.Tests;

public sealed class ApplyOverridePackFieldsRuleTests
{
    [Fact]
    public void Applies_scope_layout_metadata_replacements_and_preserved_fields()
    {
        PackageManifests previous = Create("1.0.0");
        previous.Installer.Scope = Scope.Machine;
        previous.Installer.Installers![0].InstallerSwitches = new InstallerSwitches { Silent = "/quiet" };
        previous.DefaultLocale.PublisherUrl = "http://old.example.test";
        PackageManifests current = Create("2.0.0");
        current.Installer.Scope = Scope.Machine;
        current.DefaultLocale.PublisherUrl = "http://old.example.test";
        current.DefaultLocale.Agreements =
        [
            new PackageAgreement { AgreementUrl = "http://old.example.test" },
        ];
        current.DefaultLocale.Icons =
        [
            new Icon
            {
                IconUrl = "http://old.example.test",
                IconFileType = IconFileType.Png,
            },
        ];
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            ScopeLayout = ScopeLayoutOverride.PerInstaller,
            MetadataUrlReplacements = ImmutableDictionary.CreateRange(
                [KeyValuePair.Create("http://old.example.test", "https://new.example.test")]),
            PreservedFields = ["Installers[*].InstallerSwitches"],
        };
        var context = new ManifestContext
        {
            Manifests = current,
            Previous = previous,
        };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Null(current.Installer.Scope);
        Assert.Equal(Scope.Machine, current.Installer.Installers![0].Scope);
        Assert.Equal("/quiet", current.Installer.Installers[0].InstallerSwitches?.Silent);
        Assert.Equal("https://new.example.test", current.DefaultLocale.PublisherUrl);
        Assert.Equal(
            "https://new.example.test",
            Assert.Single(current.DefaultLocale.Agreements).AgreementUrl);
        Assert.Equal(
            "https://new.example.test",
            Assert.Single(current.DefaultLocale.Icons).IconUrl);
    }

    [Fact]
    public void Explicit_drop_wins_over_preservation()
    {
        PackageManifests previous = Create("1.0.0");
        previous.DefaultLocale.ReleaseNotes = "Keep this";
        PackageManifests current = Create("2.0.0");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            PreservedFields = ["DefaultLocale.ReleaseNotes"],
            DroppedFields = ["DefaultLocale.ReleaseNotes"],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        OverridePackSet packs = new([pack]);
        RulePipeline.Create(
            [new ApplyOverridePackFieldsRule(packs)],
            new RuleRuntimeConfiguration(),
            packs).Run(context);

        Assert.Null(current.DefaultLocale.ReleaseNotes);
    }

    [Fact]
    public void Root_scope_layout_rejects_mixed_effective_scopes()
    {
        PackageManifests current = Create("2.0.0");
        current.Installer.Installers!.Add(new Installer
        {
            Architecture = Architecture.X86,
            InstallerType = InstallerType.Exe,
            Scope = Scope.User,
            InstallerUrl = "https://example.test/app-x86.exe",
            InstallerSha256 = new Sha256Hash(new string('B', 64)),
        });
        current.Installer.Installers[0].Scope = Scope.Machine;
        var context = new ManifestContext { Manifests = current };
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            ScopeLayout = ScopeLayoutOverride.Root,
        };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Contains(context.Findings, finding => finding.Message.Contains("same explicit effective scope", StringComparison.Ordinal));
        Assert.Null(current.Installer.Scope);
    }

    [Fact]
    public void Root_scope_layout_is_reapplied_after_preservation()
    {
        PackageManifests previous = Create("1.0.0");
        previous.Installer.Installers![0].Scope = Scope.Machine;
        PackageManifests current = Create("2.0.0");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            ScopeLayout = ScopeLayoutOverride.Root,
            PreservedFields = ["Installers[*].Scope"],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Equal(Scope.Machine, current.Installer.Scope);
        Assert.Null(current.Installer.Installers![0].Scope);
    }

    [Fact]
    public void Learned_value_is_stale_when_generator_no_longer_emits_reviewed_bot_value()
    {
        PackageManifests previous = Create("1.0.0");
        previous.DefaultLocale.PublisherUrl = "https://human.example.test";
        PackageManifests current = Create("2.0.0");
        current.DefaultLocale.PublisherUrl = "https://new-generator.example.test";
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            LearnedFields =
            [
                new()
                {
                    DocumentKey = "defaultLocale",
                    SemanticPath = "PublisherUrl",
                    Value = "https://human.example.test",
                    ValueSha256 = Hash("https://human.example.test"),
                    BotValueSha256 = Hash("https://old-bot.example.test"),
                    SourceFingerprint = new string('A', 64),
                    Source = "manifest:PublisherUrl",
                },
            ],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Equal("https://new-generator.example.test", current.DefaultLocale.PublisherUrl);
        Assert.Contains(
            context.Findings,
            finding => finding.Message.Contains("review it again", StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_preservation_cannot_bypass_learned_bot_value_cas()
    {
        PackageManifests previous = Create("1.0.0");
        previous.DefaultLocale.PublisherUrl = "https://human.example.test";
        PackageManifests current = Create("2.0.0");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            PreservedFields = ["DefaultLocale.PublisherUrl"],
            LearnedFields =
            [
                new()
                {
                    DocumentKey = "defaultLocale",
                    SemanticPath = "PublisherUrl",
                    Value = "https://human.example.test",
                    ValueSha256 = Hash("https://human.example.test"),
                    BotValueSha256 = Hash("https://old-bot.example.test"),
                    SourceFingerprint = new string('A', 64),
                    Source = "manifest:PublisherUrl",
                },
            ],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        OverridePackSet packs = new([pack]);
        RulePipeline.Create(
            [new ApplyOverridePackFieldsRule(packs)],
            new RuleRuntimeConfiguration(),
            packs).Run(context);

        Assert.Equal("https://human.example.test", current.DefaultLocale.PublisherUrl);
        Assert.Contains(
            context.Findings,
            finding => finding.Message.Contains("review it again", StringComparison.Ordinal));
    }

    [Fact]
    public void Earlier_production_preservation_cannot_bypass_learned_cas()
    {
        PackageManifests previous = Create("1.0.0");
        previous.DefaultLocale.Description = "human description";
        PackageManifests current = Create("2.0.0");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            LearnedFields =
            [
                new()
                {
                    DocumentKey = "defaultLocale",
                    SemanticPath = "Description",
                    Value = "human description",
                    ValueSha256 = Hash("human description"),
                    BotValueSha256 = Hash("old bot description"),
                    SourceFingerprint = new string('A', 64),
                    Source = "manifest:Description",
                },
            ],
        };
        OverridePackSet packs = new([pack]);
        var context = new ManifestContext { Manifests = current, Previous = previous };

        RulePipeline.Create(
            [
                new Meta5FieldSetParityRule(overridePacks: packs),
                new ApplyOverridePackFieldsRule(packs),
            ],
            new RuleRuntimeConfiguration(),
            packs).Run(context);

        Assert.Equal("human description", current.DefaultLocale.Description);
        Assert.Contains(
            context.Findings,
            finding => finding.Message.Contains("review it again", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_previous_locales_do_not_crash_preservation()
    {
        PackageManifests previous = Create("1.0.0");
        var locale = new LanguageTag("de-DE");
        previous.Locales =
        [
            Locale(previous, locale, "first"),
            Locale(previous, locale, "second"),
        ];
        PackageManifests current = Create("2.0.0");
        current.Locales = [Locale(current, locale, description: null)];
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            PreservedFields = ["Locales[*].Description"],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Null(Assert.Single(current.Locales).Description);
    }

    [Fact]
    public void Per_installer_drop_does_not_suppress_root_learned_value()
    {
        PackageManifests previous = Create("1.0.0");
        previous.Installer.Scope = Scope.Machine;
        PackageManifests current = Create("2.0.0");
        current.Installer.Scope = Scope.User;
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            DroppedFields = ["Installers[*].Scope"],
            LearnedFields =
            [
                new()
                {
                    DocumentKey = "installer",
                    SemanticPath = "Scope",
                    Value = "machine",
                    ValueSha256 = Hash("machine"),
                    BotValueSha256 = Hash("user"),
                    SourceFingerprint = new string('A', 64),
                    Source = "manifest:Scope",
                },
            ],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Equal(Scope.Machine, current.Installer.Scope);
    }

    [Fact]
    public void Multiple_learned_installer_fields_use_correction_independent_selector()
    {
        PackageManifests previous = Create("1.0.0");
        previous.Installer.Installers![0].Architecture = Architecture.X86;
        previous.Installer.Installers[0].Scope = Scope.Machine;
        previous.Installer.Installers.Add(new Installer
        {
            Architecture = Architecture.X64,
            InstallerType = InstallerType.Exe,
            Scope = Scope.User,
            InstallerUrl = previous.Installer.Installers[0].InstallerUrl,
            InstallerSha256 = new Sha256Hash(new string('B', 64)),
        });
        PackageManifests current = Create("2.0.0");
        current.Installer.Installers![0].Architecture = Architecture.X64;
        current.Installer.Installers[0].Scope = Scope.User;
        current.Installer.Installers.Add(new Installer
        {
            Architecture = Architecture.X64,
            InstallerType = InstallerType.Exe,
            Scope = Scope.User,
            InstallerUrl = current.Installer.Installers[0].InstallerUrl,
            InstallerSha256 = new Sha256Hash(new string('B', 64)),
        });
        string architectureSelector = LearnedInstallerSelector.Create(previous, 0, "Architecture");
        string scopeSelector = LearnedInstallerSelector.Create(previous, 0, "Scope");
        string duplicateSelector = LearnedInstallerSelector.Create(previous, 1, "Scope");
        var pack = new OverridePack
        {
            PackageIdentifier = current.Version.PackageIdentifier!,
            LearnedFields =
            [
                Learned(
                    "Installers{installer:stable#0}.Architecture",
                    "x86",
                    "x64",
                    architectureSelector,
                    lowercaseHashes: true),
                Learned(
                    "Installers{installer:stable#0}.Scope",
                    "machine",
                    "user",
                    scopeSelector),
            ],
        };
        var context = new ManifestContext { Manifests = current, Previous = previous };

        new ApplyOverridePackFieldsRule(new OverridePackSet([pack])).Apply(context);

        Assert.Equal(architectureSelector, scopeSelector);
        Assert.NotEqual(scopeSelector, duplicateSelector);
        Assert.Equal(Architecture.X86, current.Installer.Installers![0].Architecture);
        Assert.Equal(Scope.Machine, current.Installer.Installers[0].Scope);
        Assert.Empty(context.Findings);
    }

    private static LearnedFieldOverride Learned(
        string semanticPath,
        string value,
        string botValue,
        string selector,
        bool lowercaseHashes = false)
        => new()
        {
            DocumentKey = "installer",
            SemanticPath = semanticPath,
            Value = value,
            ValueSha256 = lowercaseHashes ? Hash(value).ToLowerInvariant() : Hash(value),
            BotValueSha256 = lowercaseHashes ? Hash(botValue).ToLowerInvariant() : Hash(botValue),
            SourceFingerprint = new string('A', 64),
            Source = $"manifest:{semanticPath}",
            InstallerSelectorSha256 = selector,
        };

    private static LocaleManifest Locale(
        PackageManifests manifests,
        LanguageTag locale,
        string? description)
        => new()
        {
            PackageIdentifier = manifests.Version.PackageIdentifier,
            PackageVersion = manifests.Version.PackageVersion,
            PackageLocale = locale,
            Description = description,
        };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static PackageManifests Create(string versionValue)
    {
        var identifier = new PackageIdentifier("Example.App");
        var version = new PackageVersion(versionValue);
        var locale = new LanguageTag("en-US");
        return new()
        {
            Version = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = locale,
            },
            Installer = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Exe,
                        InstallerUrl = "https://example.test/app-x64.exe",
                        InstallerSha256 = new Sha256Hash(new string('A', 64)),
                    },
                ],
            },
            DefaultLocale = new()
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = locale,
                Publisher = "Example",
                PackageName = "App",
                License = "MIT",
                ShortDescription = "Example",
            },
            Locales = [],
        };
    }
}
