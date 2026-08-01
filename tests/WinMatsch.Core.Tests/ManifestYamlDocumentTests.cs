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
}
