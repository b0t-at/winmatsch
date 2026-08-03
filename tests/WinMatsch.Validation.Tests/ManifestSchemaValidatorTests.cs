using System.Text;
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

    [Fact]
    public void Anchors_and_aliases_are_rejected_without_recursive_expansion()
    {
        const string yaml = """
            PackageIdentifier: &identity Example.App
            PackageVersion: *identity
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VLD1001", finding.Code);
        Assert.Contains("anchors and aliases", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_numeric_scalars_remain_numbers_and_explicit_string_tags_are_honored()
    {
        const string numericYaml = """
            PackageIdentifier: Example.App
            PackageVersion: 1.0
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;
        const string stringYaml = """
            PackageIdentifier: Example.App
            PackageVersion: !!str 1
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport numeric = ManifestSchemaValidator.Validate(
            new ManifestDocument("numeric.yaml", numericYaml),
            ManifestType.Version);
        ValidationReport explicitlyString = ManifestSchemaValidator.Validate(
            new ManifestDocument("string.yaml", stringYaml),
            ManifestType.Version);

        Assert.Contains(numeric.Findings, static finding => finding.Code == "VLD1003");
        Assert.True(explicitlyString.IsValid, explicitlyString.ToText());
    }

    [Fact]
    public void Duplicate_mapping_keys_return_a_diagnostic_instead_of_throwing()
    {
        const string yaml = """
            PackageIdentifier: Example.App
            PackageVersion: 1.0.0
            DefaultLocale: en-US
            DefaultLocale: de-DE
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, static finding => finding.Code == "VLD1001");
    }

    [Theory]
    [InlineData("0b10")]
    [InlineData("999999999999999999999999999999999999999999999999")]
    [InlineData("1e9999")]
    [InlineData(".inf")]
    [InlineData(".nan")]
    public void Numeric_yaml_scalars_never_downgrade_to_schema_strings(string scalar)
    {
        string yaml = $"""
            PackageIdentifier: Example.App
            PackageVersion: {scalar}
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        Assert.False(report.IsValid);
        Assert.Contains(
            report.Findings,
            static finding => finding.Code is "VLD1001" or "VLD1003");
    }

    [Fact]
    public void Huge_numeric_scalars_are_bounded_before_big_integer_conversion()
    {
        string scalar = new('9', 10_000);
        string yaml = $"""
            PackageIdentifier: Example.App
            PackageVersion: {scalar}
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VLD1001", finding.Code);
        Assert.Contains("cannot exceed 256 characters", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_tags_must_match_the_yaml_node_type()
    {
        const string yaml = """
            !!seq {
              PackageIdentifier: Example.App,
              PackageVersion: 1.0.0,
              DefaultLocale: en-US,
              ManifestType: version,
              ManifestVersion: 1.12.0
            }
            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VLD1001", finding.Code);
        Assert.Contains("incompatible with node type", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!!float 1")]
    [InlineData("!!float \"1\"")]
    public void Explicit_integer_form_floats_remain_json_numbers(string scalar)
    {
        string yaml = $"""
            PackageIdentifier: Example.App
            PackageVersion: {scalar}
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD1001");
        Assert.Contains(report.Findings, static finding => finding.Code == "VLD1003");
    }

    [Fact]
    public void Depth_budget_is_enforced_before_representation_tree_construction()
    {
        string nested = $"{new string('[', 70)}null{new string(']', 70)}";
        string yaml = $"""
            PackageIdentifier: Example.App
            PackageVersion: 1.0.0
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0
            Extra: {nested}

            """;

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VLD1001", finding.Code);
        Assert.Contains("nesting cannot exceed", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Node_budget_is_enforced_before_representation_tree_construction()
    {
        var yaml = new StringBuilder(
            """
            PackageIdentifier: Example.App
            PackageVersion: 1.0.0
            DefaultLocale: en-US
            ManifestType: version
            ManifestVersion: 1.12.0
            Extra:

            """);
        for (int index = 0; index <= 100_000; index++)
        {
            _ = yaml.AppendLine("  - {}");
        }

        ValidationReport report = ManifestSchemaValidator.Validate(
            new ManifestDocument("manifest.yaml", yaml.ToString()),
            ManifestType.Version);

        ValidationFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VLD1001", finding.Code);
        Assert.Contains("more than 100000 YAML nodes", finding.Message, StringComparison.Ordinal);
    }
}
