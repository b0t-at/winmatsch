using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class OverridePackYamlTests
{
    [Fact]
    public void Complete_override_pack_round_trips_canonically()
    {
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            RuleModes = ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [
                    KeyValuePair.Create(RuleCatalogueIds.Arch1, RuleMode.LogOnly),
                    KeyValuePair.Create(RuleIds.ApplyPackageQuirks, RuleMode.Apply),
                ]),
            ForcedArchitectures =
            [
                new()
                {
                    AssetPattern = "*-setup.exe",
                    Architecture = Architecture.X64,
                    SourceEvidence = "Repeated merged-manifest correction",
                    Confidence = RuleChangeConfidence.High,
                },
            ],
            AssetMappings =
            [
                new()
                {
                    AssetPattern = "*portable*",
                    Entry = "portable",
                    Architecture = Architecture.X64,
                    InstallerType = InstallerType.Portable,
                    Scope = Scope.User,
                },
            ],
            ScopeLayout = ScopeLayoutOverride.PerInstaller,
            VersionSource = "installer.ProductVersion",
            MetadataUrlReplacements = ImmutableDictionary.CreateRange(
                [KeyValuePair.Create("http://old.example.test", "https://new.example.test")]),
            PreservedFields = ["DefaultLocale.PublisherUrl", "Installers[*].InstallerSwitches"],
            DroppedFields = ["DefaultLocale.ReleaseNotes"],
            VanityUrls = ["https://download.example.test/latest"],
            ManualOnly = true,
            Policies =
            [
                new() { Id = RuleCatalogueIds.Arch1, Annotation = "Vendor stub is always x86." },
                new() { Id = RuleCatalogueIds.Hash2, Annotation = "Rolling URL requires review." },
            ],
            Quirks = new() { DisplayVersionFromEvidenceProperty = "Comments" },
        };

        string yaml = OverridePackYaml.Write(pack);
        OverridePack parsed = OverridePackYaml.Read(yaml);
        string rewritten = OverridePackYaml.Write(parsed);

        Assert.Equal(yaml, rewritten);
        Assert.Equal("Example.App", parsed.PackageIdentifier.Value);
        Assert.Equal(RuleMode.LogOnly, parsed.RuleModes[RuleCatalogueIds.Arch1]);
        Assert.Equal(Architecture.X64, Assert.Single(parsed.ForcedArchitectures).Architecture);
        Assert.Equal(InstallerType.Portable, Assert.Single(parsed.AssetMappings).InstallerType);
        Assert.Equal(ScopeLayoutOverride.PerInstaller, parsed.ScopeLayout);
        Assert.True(parsed.ManualOnly);
        Assert.Equal(2, parsed.Policies.Length);
        Assert.Equal("Comments", parsed.Quirks.DisplayVersionFromEvidenceProperty);
    }

    [Fact]
    public void File_read_write_round_trip_is_atomic_and_utf8()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winmatsch-rules-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "Example.App.yaml");
        var pack = new OverridePack { PackageIdentifier = new PackageIdentifier("Example.App") };
        try
        {
            OverridePackYaml.WriteFile(path, pack);
            OverridePack loaded = OverridePackYaml.ReadFile(path);

            Assert.Equal(pack.PackageIdentifier, loaded.PackageIdentifier);
            Assert.DoesNotContain('\r', File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Theory]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\nunknown: true\n")]
    [InlineData("formatVersion: one\npackageIdentifier: Test.App\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\nmanualOnly: perhaps\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\npreservedFields: value\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: &id Test.App\nversionSource: *id\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: !hostile Test.App\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\npackageIdentifier: Other.App\n")]
    public void Malformed_or_hostile_yaml_is_rejected(string yaml)
    {
        Assert.Throws<FormatException>(() => OverridePackYaml.Read(yaml));
    }

    [Fact]
    public void Oversized_yaml_is_rejected_before_parsing()
    {
        string yaml = new('x', OverridePackYaml.MaximumDocumentLength + 1);

        FormatException exception = Assert.Throws<FormatException>(() => OverridePackYaml.Read(yaml));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_file_is_bounded_while_reading()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winmatsch-hostile-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, new string('x', OverridePackYaml.MaximumDocumentLength + 1));

            FormatException exception = Assert.Throws<FormatException>(() => OverridePackYaml.ReadFile(path));

            Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Excessively_deep_yaml_is_rejected()
    {
        string nested = new string('[', OverridePackYaml.MaximumDepth + 5)
            + "\"value\""
            + new string(']', OverridePackYaml.MaximumDepth + 5);
        string yaml = $"formatVersion: 1\npackageIdentifier: Test.App\nunknown: {nested}\n";

        FormatException exception = Assert.Throws<FormatException>(() => OverridePackYaml.Read(yaml));

        Assert.Contains("depth", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Built_in_chrome_pack_is_loaded_from_yaml()
    {
        Assert.True(
            OverridePackSet.BuiltIn.TryGet(new PackageIdentifier("google.chrome"), out OverridePack? chrome));

        Assert.NotNull(chrome);
        Assert.Equal("Comments", chrome.Quirks.DisplayVersionFromEvidenceProperty);
        Assert.Contains(chrome.Policies, annotation => annotation.Id == RuleCatalogueIds.Arp1);
    }
}
