using WinMatsch.Core;
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
}
