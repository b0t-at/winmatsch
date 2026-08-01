using System.Text;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class PackageManifestIOTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"winmatsch-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WriteAndLoadDirectory_RoundTripsCompleteManifestSet()
    {
        PackageManifests expected = CreateValid();

        PackageManifestIO.WriteDirectory(_directory, expected);
        PackageManifests actual = PackageManifestIO.LoadDirectory(_directory);

        Assert.Equal(
            [
                "WinMatsch.Test.installer.yaml",
                "WinMatsch.Test.locale.de-DE.yaml",
                "WinMatsch.Test.locale.en-US.yaml",
                "WinMatsch.Test.yaml",
            ],
            Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("WinMatsch.Test", actual.Version.PackageIdentifier?.Value);
        Assert.Equal("1.2.3", actual.Installer.PackageVersion?.Value);
        Assert.Equal("en-US", actual.DefaultLocale.PackageLocale?.Value);
        Assert.Equal("de-DE", Assert.Single(actual.Locales).PackageLocale?.Value);
        Assert.All(Directory.EnumerateFiles(_directory), static path =>
        {
            string yaml = File.ReadAllText(path);
            Assert.DoesNotContain('\r', yaml);
            Assert.EndsWith("\n", yaml, StringComparison.Ordinal);
            Assert.False(yaml.EndsWith("\n\n", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void LoadDirectory_RejectsIncorrectFilename()
    {
        PackageManifestIO.WriteDirectory(_directory, CreateValid());
        File.Move(
            Path.Combine(_directory, "WinMatsch.Test.installer.yaml"),
            Path.Combine(_directory, "wrong.installer.yaml"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("must be 'WinMatsch.Test.installer.yaml'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_RejectsMissingSchemaVersionHeader()
    {
        PackageManifestIO.WriteDirectory(_directory, CreateValid());
        string path = Path.Combine(_directory, "WinMatsch.Test.yaml");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("ManifestVersion: 1.12.0\n", "", StringComparison.Ordinal));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("has no ManifestVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_RejectsNonCanonicalManifestType()
    {
        PackageManifestIO.WriteDirectory(_directory, CreateValid());
        string path = Path.Combine(_directory, "WinMatsch.Test.yaml");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("ManifestType: version", "ManifestType: Version", StringComparison.Ordinal));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("expected 'version'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_ToleratesUnknownKeys()
    {
        PackageManifestIO.WriteDirectory(_directory, CreateValid());
        string path = Path.Combine(_directory, "WinMatsch.Test.yaml");
        File.AppendAllText(path, "SomeFutureField: retained-by-reader-contract\n");

        PackageManifests manifests = PackageManifestIO.LoadDirectory(_directory);

        Assert.Equal("WinMatsch.Test", manifests.Version.PackageIdentifier?.Value);
    }

    [Fact]
    public void Validate_RejectsIdentifierDisagreement()
    {
        PackageManifests manifests = CreateValid();
        manifests.Installer.PackageIdentifier = new PackageIdentifier("WinMatsch.Other");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("PackageIdentifier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsVersionDisagreement()
    {
        PackageManifests manifests = CreateValid();
        manifests.DefaultLocale.PackageVersion = new PackageVersion("2.0.0");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("PackageVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsSchemaVersionDisagreement()
    {
        PackageManifests manifests = CreateValid();
        manifests.Locales[0].ManifestVersion = new ManifestVersion("1.11.0");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("ManifestVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsManifestTypeDisagreement()
    {
        PackageManifests manifests = CreateValid();
        manifests.Installer.ManifestType = ManifestType.Locale;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("expected 'installer'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsDefaultLocaleDisagreement()
    {
        PackageManifests manifests = CreateValid();
        manifests.Version.DefaultLocale = new LanguageTag("fr-FR");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("does not match version manifest DefaultLocale", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsDuplicateLocale()
    {
        PackageManifests manifests = CreateValid();
        manifests.Locales.Add(CreateLocale("DE-de"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.Validate(manifests));

        Assert.Contains("appears more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeFiles_RejectsUnsupportedSchemaVersion()
    {
        PackageManifests manifests = CreateValid();
        var future = new ManifestVersion("1.13.0");
        manifests.Version.ManifestVersion = future;
        manifests.Installer.ManifestVersion = future;
        manifests.DefaultLocale.ManifestVersion = future;
        manifests.Locales[0].ManifestVersion = future;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.SerializeFiles(manifests));

        Assert.Contains("can only be written as schema 1.12.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDirectory_RejectsUnexpectedYamlFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "stale.locale.fr-FR.yaml"), "ManifestType: locale\n");

        IOException exception = Assert.Throws<IOException>(
            () => PackageManifestIO.WriteDirectory(_directory, CreateValid()));

        Assert.Contains("unexpected YAML file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDirectory_AtomicallyReplacesExistingManifestFiles()
    {
        PackageManifests manifests = CreateValid();
        PackageManifestIO.WriteDirectory(_directory, manifests);
        manifests.DefaultLocale.Publisher = "Updated publisher";

        PackageManifestIO.WriteDirectory(_directory, manifests);

        PackageManifests loaded = PackageManifestIO.LoadDirectory(_directory);
        Assert.Equal("Updated publisher", loaded.DefaultLocale.Publisher);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_directory),
            static path => Path.GetExtension(path) is ".tmp" or ".bak");
    }

    [Fact]
    public void WriteDirectory_Preserves_existing_line_endings_and_uses_lf_for_new_files()
    {
        PackageManifests manifests = CreateValid();
        manifests.Locales.Clear();
        PackageManifestIO.WriteDirectory(_directory, manifests);
        foreach (string path in Directory.EnumerateFiles(_directory))
        {
            string lf = File.ReadAllText(path);
            File.WriteAllText(path, lf.Replace("\n", "\r\n", StringComparison.Ordinal));
        }

        manifests.DefaultLocale.Publisher = "Updated";
        manifests.Locales.Add(CreateLocale("fr-FR"));
        PackageManifestIO.WriteDirectory(_directory, manifests);

        byte[] existing = File.ReadAllBytes(
            Path.Combine(_directory, "WinMatsch.Test.locale.en-US.yaml"));
        byte[] added = File.ReadAllBytes(
            Path.Combine(_directory, "WinMatsch.Test.locale.fr-FR.yaml"));
        Assert.True(existing.AsSpan().IndexOf("\r\n"u8) >= 0);
        AssertNoBareLineFeeds(existing);
        Assert.DoesNotContain((byte)'\r', added);
        Assert.Equal((byte)'\n', added[^1]);
    }

    [Fact]
    public void Empty_schema_valid_collections_and_mappings_round_trip()
    {
        PackageManifests manifests = CreateValid();
        manifests.DefaultLocale.Tags = [];
        manifests.Installer.Commands = [];
        manifests.Installer.Dependencies = new Dependencies();
        manifests.Installer.InstallerSwitches = new InstallerSwitches();

        IReadOnlyDictionary<string, string> files = PackageManifestIO.SerializeFiles(manifests);
        string locale = files["WinMatsch.Test.locale.en-US.yaml"];
        string installer = files["WinMatsch.Test.installer.yaml"];
        Assert.Contains("Tags: []\n", locale, StringComparison.Ordinal);
        Assert.Contains("Commands: []\n", installer, StringComparison.Ordinal);
        Assert.Contains("Dependencies: {}\n", installer, StringComparison.Ordinal);
        Assert.Contains("InstallerSwitches: {}\n", installer, StringComparison.Ordinal);

        PackageManifestIO.WriteDirectory(_directory, manifests);
        PackageManifests loaded = PackageManifestIO.LoadDirectory(_directory);
        Assert.Empty(loaded.DefaultLocale.Tags!);
        Assert.Empty(loaded.Installer.Commands!);
        Assert.NotNull(loaded.Installer.Dependencies);
        Assert.NotNull(loaded.Installer.InstallerSwitches);
    }

    [Fact]
    public void LoadDirectory_Rejects_files_above_the_manifest_byte_budget()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "oversized.yaml");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(ManifestYamlDocument.MaxManifestBytes + 1L);
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_Rejects_excessive_directory_entries_before_sorting()
    {
        Directory.CreateDirectory(_directory);
        for (int index = 0; index <= ManifestYamlDirectory.MaxDirectoryEntries; index++)
        {
            File.WriteAllBytes(Path.Combine(_directory, $"{index:D4}.txt"), []);
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("manifest directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_Rejects_excessive_manifest_file_count_before_parsing()
    {
        Directory.CreateDirectory(_directory);
        for (int index = 0; index <= ManifestYamlDirectory.MaxManifestFiles; index++)
        {
            File.WriteAllText(
                Path.Combine(_directory, $"{index:D3}.yaml"),
                "Value: test\n");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("YAML files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_Rejects_aggregate_yaml_resource_exhaustion()
    {
        Directory.CreateDirectory(_directory);
        var yaml = new StringBuilder("Values:\n");
        for (int index = 0; index < 70_000; index++)
        {
            _ = yaml.AppendLine("- {}");
        }

        for (int index = 0; index < 3; index++)
        {
            File.WriteAllText(
                Path.Combine(_directory, $"{index}.yaml"),
                yaml.ToString());
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("aggregate YAML resource budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_Rejects_hostile_depth_before_tree_materialization()
    {
        Directory.CreateDirectory(_directory);
        string nested = $"{new string('[', 70)}null{new string(']', 70)}";
        File.WriteAllText(
            Path.Combine(_directory, "hostile.yaml"),
            $"ManifestType: version\nExtra: {nested}\n");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => PackageManifestIO.LoadDirectory(_directory));

        Assert.Contains("nesting cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirectory_Rejects_a_reparse_point_directory()
    {
        string target = Path.Combine(Path.GetTempPath(), $"winmatsch-link-target-{Guid.NewGuid():N}");
        string link = Path.Combine(_directory, "linked");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_directory);
        try
        {
            PackageManifestIO.WriteDirectory(target, CreateValid());
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                return;
            }

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => PackageManifestIO.LoadDirectory(link));

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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static PackageManifests CreateValid()
    {
        var identifier = new PackageIdentifier("WinMatsch.Test");
        var version = new PackageVersion("1.2.3");

        return new PackageManifests
        {
            Installer = new InstallerManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                InstallerType = InstallerType.Exe,
                Installers =
                [
                    new Installer
                    {
                        Architecture = Architecture.X64,
                        InstallerUrl = "https://example.com/test.exe",
                        InstallerSha256 = new Sha256Hash(
                            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"),
                    },
                ],
            },
            DefaultLocale = new DefaultLocaleManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                PackageLocale = new LanguageTag("en-US"),
                Publisher = "WinMatsch",
                PackageName = "Test package",
                License = "MIT",
                ShortDescription = "Test package",
            },
            Locales = [CreateLocale("de-DE")],
            Version = new VersionManifest
            {
                PackageIdentifier = identifier,
                PackageVersion = version,
                DefaultLocale = new LanguageTag("en-US"),
            },
        };
    }

    private static LocaleManifest CreateLocale(string locale)
        => new()
        {
            PackageIdentifier = new PackageIdentifier("WinMatsch.Test"),
            PackageVersion = new PackageVersion("1.2.3"),
            PackageLocale = new LanguageTag(locale),
        };

    private static void AssertNoBareLineFeeds(ReadOnlySpan<byte> content)
    {
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] == (byte)'\n')
            {
                Assert.True(index > 0 && content[index - 1] == (byte)'\r');
            }
        }
    }
}
