using System.Collections.Immutable;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using Xunit;

namespace WinMatsch.Rules.Tests;

public class OverridePackYamlTests
{
    [Theory]
    [InlineData("installer.fileVersion")]
    [InlineData("file-version")]
    public void File_version_sources_are_supported(string versionSource)
    {
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            VersionSource = versionSource,
        };

        OverridePack parsed = OverridePackYaml.Read(OverridePackYaml.Write(pack));

        Assert.Equal(versionSource, parsed.VersionSource);
    }

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
            LearnedFields =
            [
                new()
                {
                    DocumentKey = "installer",
                    SemanticPath = "Installers{installer:ABC#0}.Scope",
                    Value = "Machine",
                    ValueSha256 = new string('A', 64),
                    BotValueSha256 = new string('C', 64),
                    SourceFingerprint = new string('B', 64),
                    Source = "manifests/e/Example/App/1.0.0/Example.App.installer.yaml:Installers[0].Scope",
                    InstallerSelectorSha256 = new string('D', 64),
                },
            ],
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
        Assert.Equal("Machine", Assert.Single(parsed.LearnedFields).Value);
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Concurrent_learned_memory_writes_leave_one_complete_parseable_pack(int iteration)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-rules-memory-{iteration}-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "Example.Memory.yaml");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task[] writers =
            [
                .. Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
                {
                    await start.Task;
                    OverridePackYaml.WriteFile(path, new OverridePack
                    {
                        PackageIdentifier = new PackageIdentifier("Example.Memory"),
                        VersionSource = $"literal:{index}.0.0",
                    });
                })),
            ];

            start.SetResult();
            await Task.WhenAll(writers);

            OverridePack loaded = OverridePackYaml.ReadFile(path);
            Assert.Equal("Example.Memory", loaded.PackageIdentifier.Value);
            Assert.StartsWith("literal:", loaded.VersionSource, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
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

    [Theory]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\npreservedFields:\n  - Unknown.Path\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\nmetadataUrlReplacements:\n  \"https://old.example.test\": \"http://new.example.test\"\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\nmetadataUrlReplacements:\n  \"https://old.example.test?a=secret\": \"https://new.example.test\"\n")]
    [InlineData("formatVersion: 1\npackageIdentifier: Test.App\nversionSource: unknown\n")]
    public void Unsafe_or_unsupported_override_semantics_are_rejected(string yaml)
    {
        FormatException exception = Assert.Throws<FormatException>(() => OverridePackYaml.Read(yaml));

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void Unsafe_metadata_url_error_does_not_echo_query_secrets()
    {
        const string secret = "sensitive-token";
        string yaml = $"formatVersion: 1\npackageIdentifier: Test.App\nmetadataUrlReplacements:\n  \"https://old.example.test?a={secret}\": \"https://new.example.test\"\n";

        FormatException exception = Assert.Throws<FormatException>(() => OverridePackYaml.Read(yaml));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
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
        Assert.DoesNotContain(chrome.Policies, annotation => annotation.Id == RuleCatalogueIds.Arp1);
    }

    [Fact]
    public void Override_pack_enumeration_is_immutable_and_consistent_with_lookup()
    {
        var first = new OverridePack { PackageIdentifier = new PackageIdentifier("Example.First") };
        var second = new OverridePack { PackageIdentifier = new PackageIdentifier("Example.Second") };
        var set = new OverridePackSet([first, second]);

        object exposed = set.Packs;
        Assert.False(exposed is OverridePack[]);
        Assert.True(set.TryGet(first.PackageIdentifier, out OverridePack? found));
        Assert.Same(first, found);
        Assert.Contains(found, set.Packs);
    }

    [Fact]
    public void Writer_rejects_reader_invalid_scalars_before_serialization()
    {
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            VersionSource = new string('x', OverridePackYaml.MaximumScalarLength + 1),
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => OverridePackYaml.Write(pack));

        Assert.Contains("scalar limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_rejects_excessive_node_counts_before_serialization()
    {
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            PreservedFields =
            [
                .. Enumerable.Range(0, OverridePackYaml.MaximumNodeCount)
                    .Select(static _ => "DefaultLocale.Description"),
            ],
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => OverridePackYaml.Write(pack));

        Assert.Contains("node count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_rejects_output_that_would_exceed_the_document_limit()
    {
        string large = new('x', OverridePackYaml.MaximumScalarLength);
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            Policies =
            [
                .. Enumerable.Range(0, 16).Select(index => new PolicyAnnotation
                {
                    Id = $"TEST-{index}",
                    Annotation = large,
                }),
            ],
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => OverridePackYaml.Write(pack));

        Assert.Contains("output limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_rejects_enum_values_the_reader_would_reject()
    {
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            ScopeLayout = (ScopeLayoutOverride)999,
        };

        Assert.Throws<ArgumentException>(() => OverridePackYaml.Write(pack));
    }

    [Fact]
    public void Yaml_line_break_characters_are_escaped_and_round_trip_exactly()
    {
        const string annotation = "next\u0085line\u2028separator\u2029paragraph";
        var pack = new OverridePack
        {
            PackageIdentifier = new PackageIdentifier("Example.App"),
            Policies = [new() { Id = RuleCatalogueIds.Pipe1, Annotation = annotation }],
        };

        string yaml = OverridePackYaml.Write(pack);
        OverridePack parsed = OverridePackYaml.Read(yaml);

        Assert.DoesNotContain('\u0085', yaml);
        Assert.DoesNotContain('\u2028', yaml);
        Assert.DoesNotContain('\u2029', yaml);
        Assert.Contains("\\u0085", yaml, StringComparison.Ordinal);
        Assert.Contains("\\u2028", yaml, StringComparison.Ordinal);
        Assert.Contains("\\u2029", yaml, StringComparison.Ordinal);
        Assert.Equal(annotation, Assert.Single(parsed.Policies).Annotation);
    }
}
