using System.Text;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class ManifestYamlDocumentTests
{
    [Fact]
    public void File_reader_rejects_paths_outside_the_allowed_root()
    {
        string root = Path.Combine(Path.GetTempPath(), $"winmatsch-yaml-root-{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"winmatsch-yaml-outside-{Guid.NewGuid():N}.yaml");
        Directory.CreateDirectory(root);
        File.WriteAllText(outside, "Value: test\n");
        try
        {
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => ManifestYamlDocument.ReadTextFile(outside, root));

            Assert.Contains("must remain inside", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Scalar_budget_is_enforced_before_tree_materialization()
    {
        var yaml = new StringBuilder("Values:\n");
        for (int index = 0; index <= ManifestYamlDocument.MaxYamlScalars; index++)
        {
            _ = yaml.AppendLine("- value");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ManifestYamlDocument.Parse(yaml.ToString()));

        Assert.Contains("YAML scalars", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_tag_budget_is_enforced_before_tree_materialization()
    {
        var yaml = new StringBuilder("Values:\n");
        for (int index = 0; index <= ManifestYamlDocument.MaxYamlTags; index++)
        {
            _ = yaml.AppendLine("- !!str value");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ManifestYamlDocument.Parse(yaml.ToString()));

        Assert.Contains("explicit YAML tags", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_tags_are_rejected()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ManifestYamlDocument.Parse("Value: !custom data\n"));

        Assert.Contains("YAML tag", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_budget_is_enforced_before_tree_materialization()
    {
        var yaml = new StringBuilder();
        for (int index = 0; index < 70_001; index++)
        {
            _ = yaml.AppendLine("---").AppendLine("...");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ManifestYamlDocument.Parse(yaml.ToString()));

        Assert.Contains("YAML events", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decoded_byte_order_mark_does_not_rename_the_first_key()
    {
        // Niklas2233.CarBudget 0.2.0 is published with a UTF-8 BOM; the identity check read
        // "\uFEFFPackageIdentifier" and rejected the manifest.
        ManifestHeader header = ManifestYamlReader.ReadHeader(
            "\uFEFFPackageIdentifier: Niklas2233.CarBudget\nPackageVersion: 0.2.0\nManifestType: installer\n");

        Assert.Equal("Niklas2233.CarBudget", header.PackageIdentifier);
        Assert.Equal("0.2.0", header.PackageVersion);
    }

    [Fact]
    public void Duplicate_root_keys_are_rejected_unless_repaired_for_reading()
    {
        // CarthageSoftware.Mago 1.47.2 was merged with ReleaseNotesUrl twice.
        const string yaml = """
            PackageIdentifier: CarthageSoftware.Mago
            PackageVersion: 1.47.2
            ReleaseNotesUrl: https://example.test/releases/tag/1.42.0
            Documentations:
            - DocumentLabel: Documentation
              DocumentUrl: https://example.test/docs
            ReleaseNotes: |-
              # Mago 1.47.2
            ReleaseNotesUrl: https://example.test/releases/tag/1.47.2
            ManifestType: defaultLocale
            ManifestVersion: 1.12.0

            """;
        Assert.ThrowsAny<Exception>(() => ManifestYamlDocument.Parse(yaml));

        string repaired = ManifestYamlText.RepairForReading(yaml, out IReadOnlyList<string> removed);

        Assert.Equal(["ReleaseNotesUrl"], removed);
        ManifestYamlDocument document = ManifestYamlDocument.Parse(repaired);
        DefaultLocaleManifest locale = ManifestYamlReader.ReadDefaultLocale(document.Content);
        Assert.Equal("https://example.test/releases/tag/1.47.2", locale.ReleaseNotesUrl);
        Assert.Equal("https://example.test/docs", Assert.Single(locale.Documentations!).DocumentUrl);
        Assert.Equal("# Mago 1.47.2", locale.ReleaseNotes);
    }

    [Fact]
    public void Repair_for_reading_leaves_well_formed_manifests_untouched()
    {
        const string yaml = "PackageIdentifier: Example.App\nPackageVersion: 1.0\nManifestType: version\n";

        string repaired = ManifestYamlText.RepairForReading(yaml, out IReadOnlyList<string> removed);

        Assert.Same(yaml, repaired);
        Assert.Empty(removed);
    }
}
