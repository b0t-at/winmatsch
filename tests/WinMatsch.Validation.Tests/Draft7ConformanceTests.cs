using System.Text.Json;
using WinMatsch.Validation.Schema;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class Draft7ConformanceTests
{
    [Fact]
    public void Official_draft7_subset_conforms_for_every_gate_accepted_group()
    {
        string fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "JsonSchemaTestSuite");
        string[] fixtureFiles = Directory.GetFiles(fixtureDirectory, "*.json");
        Array.Sort(fixtureFiles, StringComparer.Ordinal);

        int acceptedGroups = 0;
        int skippedGroups = 0;
        int evaluatedCases = 0;
        var failures = new List<string>();
        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string fixtureFile in fixtureFiles)
        {
            using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(fixtureFile));
            foreach (JsonElement group in fixture.RootElement.EnumerateArray())
            {
                string groupDescription = group.GetProperty("description").GetString()!;
                Draft7Schema schema;
                try
                {
                    schema = Draft7SchemaCompiler.Compile(
                        group.GetProperty("schema").GetRawText(),
                        $"{Path.GetFileName(fixtureFile)}: {groupDescription}");
                }
                catch (InvalidOperationException exception)
                    when (IsExpectedGateRejection(exception.Message, out string reason))
                {
                    skippedGroups++;
                    skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
                    continue;
                }

                acceptedGroups++;
                foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
                {
                    evaluatedCases++;
                    bool expected = test.GetProperty("valid").GetBoolean();
                    Draft7EvaluationResult result = Draft7Evaluator.Evaluate(
                        schema,
                        test.GetProperty("data"));
                    if (result.IsValid != expected)
                    {
                        failures.Add(
                            $"{Path.GetFileName(fixtureFile)} :: {groupDescription} :: "
                            + $"{test.GetProperty("description").GetString()} expected "
                            + $"{expected}, got {result.IsValid}: {FormatErrors(result)}");
                    }
                }
            }
        }

        string summary = $"Accepted {acceptedGroups} groups / {evaluatedCases} cases; "
            + $"gate-skipped {skippedGroups} groups"
            + (skipReasons.Count == 0
                ? "."
                : $" ({string.Join(", ", skipReasons.OrderBy(static item => item.Key).Select(static item => $"{item.Key}: {item.Value}"))}).");
        Console.WriteLine(summary);

        Assert.NotEmpty(fixtureFiles);
        Assert.True(acceptedGroups > 0, summary);
        Assert.True(evaluatedCases > 0, summary);
        Assert.True(
            failures.Count == 0,
            $"{summary}{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
    }

    private static bool IsExpectedGateRejection(string message, out string reason)
    {
        (string Snippet, string Reason)[] expected =
        [
            ("outside the supported Draft-07 subset", "unsupported-keyword"),
            ("tuple-form 'items'", "tuple-items"),
            ("boolean and non-object schemas", "boolean-schema"),
            ("supported at the schema root only", "nested-root-keyword"),
            ("only unencoded internal references", "unsupported-reference"),
            ("assertion keywords beside '$ref'", "ref-sibling"),
            ("forms a non-progressing cycle", "cyclic-reference"),
        ];

        foreach ((string snippet, string category) in expected)
        {
            if (message.Contains(snippet, StringComparison.Ordinal))
            {
                reason = category;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static string FormatErrors(Draft7EvaluationResult result)
        => string.Join(
            "; ",
            result.Errors.Select(static error => $"{error.InstanceLocation} {error.Keyword}: {error.Message}"));
}
