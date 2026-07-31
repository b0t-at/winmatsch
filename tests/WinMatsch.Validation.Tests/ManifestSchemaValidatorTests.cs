using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class ManifestSchemaValidatorTests
{
    [Fact]
    public void All_four_bundled_1_12_schemas_accept_canonical_manifests()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Locales.Add(new LocaleManifest
        {
            PackageIdentifier = manifests.Version.PackageIdentifier,
            PackageVersion = manifests.Version.PackageVersion,
            PackageLocale = new LanguageTag("de-DE"),
            PackageName = "Beispiel",
            ShortDescription = "Beispielanwendung",
        });
        IReadOnlyDictionary<string, string> files = PackageManifestIO.SerializeFiles(manifests);

        ValidationReport[] reports =
        [
            ManifestSchemaValidator.Validate(
                new ManifestDocument("version.yaml", files[$"{TestPackageFactory.Identifier}.yaml"]),
                ManifestType.Version),
            ManifestSchemaValidator.Validate(
                new ManifestDocument("installer.yaml", files[$"{TestPackageFactory.Identifier}.installer.yaml"]),
                ManifestType.Installer),
            ManifestSchemaValidator.Validate(
                new ManifestDocument("default.yaml", files[$"{TestPackageFactory.Identifier}.locale.en-US.yaml"]),
                ManifestType.DefaultLocale),
            ManifestSchemaValidator.Validate(
                new ManifestDocument("locale.yaml", files[$"{TestPackageFactory.Identifier}.locale.de-DE.yaml"]),
                ManifestType.Locale),
        ];

        Assert.All(reports, static report => Assert.True(report.IsValid, report.ToText()));
    }

    [Fact]
    public void Schema_rejects_missing_required_field()
    {
        const string yaml = """
            PackageIdentifier: Example.App
            PackageVersion: 1.0.0
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(
            report.Findings,
            static finding => finding.Code == "VLD1003");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
    }

    [Fact]
    public void Schema_reports_exact_property_casing()
    {
        string yaml = ManifestYamlWriter.Serialize(TestPackageFactory.CreateManifests().Version)
            .Replace("PackageIdentifier:", "packageidentifier:", StringComparison.Ordinal);

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        Assert.Contains(
            report.Findings,
            static finding => finding.Code == "VLD1002"
                && finding.Message.Contains("PackageIdentifier", StringComparison.Ordinal));
    }
}
