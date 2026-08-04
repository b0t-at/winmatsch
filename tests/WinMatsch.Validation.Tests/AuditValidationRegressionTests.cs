using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class AuditValidationRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"winmatsch-validation-audit-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(20)]
    [InlineData(64)]
    [InlineData(256)]
    public async Task Oversized_installer_success_codes_never_escape_validation(int digits)
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument[] documents =
        [
            .. valid.Documents.Select(document =>
                document.RepositoryPath.Contains(".installer.", StringComparison.Ordinal)
                    ? document with
                    {
                        Content = document.Content.Replace(
                            "Installers:\n",
                            $"InstallerSuccessCodes:\n- {new string('9', digits)}\nInstallers:\n",
                            StringComparison.Ordinal),
                    }
                    : document),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents));

        Assert.False(report.IsValid);
        Assert.Contains(
            report.Findings,
            static finding => finding.Code is "VLD1001" or "VLD1003" or "VLD2003");
    }

    [Fact]
    public async Task Schema_header_is_accepted_after_leading_generator_comments()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument first = valid.Documents[0];
        ManifestDocument[] documents =
        [
            first with { Content = $"# Created with another tool\n{first.Content}" },
            .. valid.Documents.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents));

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD2104");
    }

    [Fact]
    public async Task Schema_header_after_manifest_content_is_rejected()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument first = valid.Documents[0];
        int newline = first.Content.IndexOf('\n');
        string header = first.Content[..newline];
        string body = first.Content[(newline + 1)..];
        ManifestDocument[] documents =
        [
            first with { Content = $"{body}\n{header}\n" },
            .. valid.Documents.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents));

        ValidationFinding finding = Assert.Single(
            report.Findings,
            static finding => finding.Code == "VLD2104");
        Assert.Contains("before manifest content", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_schema_version_comes_from_the_core_default()
    {
        Assert.Equal(ManifestVersion.Default.Value, ManifestSchemaValidator.SchemaVersion);
    }

    [Fact]
    public async Task Unknown_scope_and_locale_match_known_values()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Scope = null;
        List<Installer> installers = manifests.Installer.Installers!;
        Installer first = Assert.Single(installers);
        first.InstallerLocale = null;
        installers.Add(CreateInstaller(
            "https://example.com/user-en.exe",
            scope: Scope.User,
            locale: new LanguageTag("en-US")));

        ValidationReport report = await Validate(manifests);

        Assert.Single(report.Findings, static finding => finding.Code == "VLD3001");
    }

    [Fact]
    public async Task Known_different_scope_and_locale_values_remain_distinct()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Scope = null;
        List<Installer> installers = manifests.Installer.Installers!;
        Installer first = Assert.Single(installers);
        first.Scope = Scope.User;
        first.InstallerLocale = new LanguageTag("en-US");
        installers.Add(CreateInstaller(
            "https://example.com/machine-de.exe",
            scope: Scope.Machine,
            locale: new LanguageTag("de-DE")));

        ValidationReport report = await Validate(manifests);

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3001");
    }

    [Fact]
    public async Task Root_inheritance_participates_in_duplicate_detection()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.InstallerLocale = new LanguageTag("en-US");
        List<Installer> installers = manifests.Installer.Installers!;
        _ = Assert.Single(installers);
        installers.Add(CreateInstaller(
            "https://example.com/explicit.exe",
            installerType: InstallerType.Exe,
            scope: Scope.Machine,
            locale: new LanguageTag("en-US")));

        ValidationReport report = await Validate(manifests);

        Assert.Single(report.Findings, static finding => finding.Code == "VLD3001");
    }

    [Fact]
    public async Task Different_nested_installer_types_are_legal_archive_variants()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Zip;
        List<Installer> installers = manifests.Installer.Installers!;
        Installer first = Assert.Single(installers);
        first.NestedInstallerType = InstallerType.Portable;
        first.NestedInstallerFiles =
        [
            new NestedInstallerFile
            {
                RelativeFilePath = "bin/tool.exe",
                PortableCommandAlias = "tool",
            },
        ];
        Installer second = CreateInstaller(
            "https://example.com/setup-msi.zip",
            nestedType: InstallerType.Msi);
        second.NestedInstallerFiles =
        [
            new NestedInstallerFile { RelativeFilePath = "setup.msi" },
        ];
        installers.Add(second);

        ValidationReport report = await Validate(manifests);

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3001");
    }

    [Fact]
    public async Task Multi_locale_installers_with_the_same_other_dimensions_are_distinct()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        List<Installer> installers = manifests.Installer.Installers!;
        Installer first = Assert.Single(installers);
        first.InstallerLocale = new LanguageTag("en-US");
        installers.Add(CreateInstaller(
            "https://example.com/de.exe",
            locale: new LanguageTag("de-DE")));

        ValidationReport report = await Validate(manifests);

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3001");
    }

    [Fact]
    public async Task Schema_valid_empty_collections_and_mappings_remain_valid()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.DefaultLocale.Tags = [];
        manifests.Installer.Commands = [];
        manifests.Installer.Dependencies = new Dependencies();
        manifests.Installer.InstallerSwitches = new InstallerSwitches();

        ValidationReport report = await Validate(manifests);

        Assert.DoesNotContain(
            report.Findings,
            static finding => finding.Code.StartsWith("VLD1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wildcard_bridge_does_not_merge_known_different_scopes()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Scope = null;
        List<Installer> installers = manifests.Installer.Installers!;
        installers[0].Scope = Scope.User;
        installers.Add(CreateInstaller("https://example.com/machine.exe", scope: Scope.Machine));
        installers.Add(CreateInstaller("https://example.com/unknown.exe"));

        ValidationReport report = await Validate(manifests);

        ValidationFinding finding = Assert.Single(
            report.Findings,
            static finding => finding.Code == "VLD3001");
        Assert.Equal("Installers[2]", finding.Path);
    }

    [Fact]
    public async Task Unparseable_display_versions_do_not_create_ordinal_ranges()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        Assert.Single(manifests.Installer.Installers!).AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry { DisplayVersion = "release/alpha" },
            new AppsAndFeaturesEntry { DisplayVersion = "release/zulu" },
        ];
        ExistingVersionSnapshot[] existing =
        [
            new("1.0.0", ["release/middle"]),
        ];

        ValidationReport report = await Validate(manifests, existing);

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3101");
    }

    [Fact]
    public async Task Exact_unparseable_display_version_overlap_is_still_blocking()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        Assert.Single(manifests.Installer.Installers!).AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry { DisplayVersion = "release/alpha" },
        ];
        ExistingVersionSnapshot[] existing =
        [
            new("1.0.0", ["RELEASE/ALPHA"]),
        ];

        ValidationReport report = await Validate(manifests, existing);

        Assert.Single(report.Findings, static finding => finding.Code == "VLD3101");
    }

    [Fact]
    public void FromDirectory_rejects_oversized_yaml_before_reading_it_as_text()
    {
        string version = Path.Combine(_root, "version");
        Directory.CreateDirectory(version);
        using (FileStream stream = File.Create(Path.Combine(version, "oversized.yaml")))
        {
            stream.SetLength(ManifestYamlDocument.MaxManifestBytes + 1L);
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PreflightRequest.FromDirectory(_root, version, []));

        Assert.Contains("cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDirectory_rejects_hostile_depth_before_tree_materialization()
    {
        string version = Path.Combine(_root, "version");
        Directory.CreateDirectory(version);
        string nested = $"{new string('[', 70)}null{new string(']', 70)}";
        File.WriteAllText(
            Path.Combine(version, "hostile.yaml"),
            $"ManifestType: version\nExtra: {nested}\n");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PreflightRequest.FromDirectory(_root, version, []));

        Assert.Contains("nesting cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDirectory_rejects_reparse_point_traversal()
    {
        string target = Path.Combine(Path.GetTempPath(), $"winmatsch-preflight-target-{Guid.NewGuid():N}");
        string link = Path.Combine(_root, "version");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "manifest.yaml"), "ManifestType: version\n");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                return;
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => PreflightRequest.FromDirectory(_root, link, []));

            Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Nested_uniqueness_messages_describe_the_actual_local_list_scope()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Zip;
        Installer installer = Assert.Single(manifests.Installer.Installers!);
        installer.NestedInstallerType = InstallerType.Portable;
        installer.NestedInstallerFiles =
        [
            new NestedInstallerFile
            {
                RelativeFilePath = "bin/tool.exe",
                PortableCommandAlias = "tool",
            },
            new NestedInstallerFile
            {
                RelativeFilePath = "BIN/TOOL.EXE",
                PortableCommandAlias = "TOOL",
            },
        ];

        ValidationReport report = await Validate(manifests);

        Assert.Contains(
            report.Findings,
            static finding => finding.Code == "VLD3006"
                && finding.Message.Contains("this NestedInstallerFiles list", StringComparison.Ordinal));
        Assert.Contains(
            report.Findings,
            static finding => finding.Code == "VLD3010"
                && finding.Message.Contains("this NestedInstallerFiles list", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Task<ValidationReport> Validate(
        PackageManifests manifests,
        IReadOnlyList<ExistingVersionSnapshot>? existing = null)
        => new PreflightGate(new FakePreflightNetwork()).ValidateAsync(
            TestPackageFactory.CreateRequest(
                manifests,
                existingVersions: existing));

    private static Installer CreateInstaller(
        string url,
        InstallerType? installerType = null,
        InstallerType? nestedType = null,
        Scope? scope = null,
        LanguageTag? locale = null)
        => new()
        {
            Architecture = Architecture.X64,
            InstallerType = installerType,
            NestedInstallerType = nestedType,
            Scope = scope,
            InstallerLocale = locale,
            InstallerUrl = url,
            InstallerSha256 = new Sha256Hash(TestPackageFactory.Hash),
        };

    private static PreflightRequest Copy(
        PreflightRequest source,
        IReadOnlyList<ManifestDocument> documents)
        => new()
        {
            Documents = documents,
            Changes = source.Changes,
            ExistingVersions = source.ExistingVersions,
            InstallerArtifacts = source.InstallerArtifacts,
            Options = source.Options,
        };
}
