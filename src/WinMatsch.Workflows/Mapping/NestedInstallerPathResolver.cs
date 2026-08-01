using System.Collections.Immutable;

namespace WinMatsch.Workflows.Mapping;

internal static class NestedInstallerPathResolver
{
    public static NestedPathResolution Resolve(
        PreviousInstallerEntry? previous,
        AssetAnalysisEvidence? analysis,
        string newVersion)
    {
        if (analysis is null)
        {
            return previous is { NestedInstallerFiles.IsEmpty: false }
                ? NestedPathResolution.Unresolved("NESTED_REANALYSIS_REQUIRED", "Archive contents are unavailable.")
                : NestedPathResolution.Empty;
        }

        string[] actualPaths = analysis.ArchiveEntries
            .Concat(analysis.NestedInstallerCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actualPaths.Length == 0)
        {
            return previous is { NestedInstallerFiles.IsEmpty: false }
                ? NestedPathResolution.Unresolved("NESTED_PATH_REMOVED", "No bounded archive entry matches the previous nested installer.")
                : NestedPathResolution.Empty;
        }

        if (previous is null || previous.NestedInstallerFiles.IsEmpty)
        {
            return analysis.NestedInstallerCandidates.Length == 1
                ? new(
                    [new PlannedNestedInstallerFile(analysis.NestedInstallerCandidates[0], null)],
                    null,
                    null)
                : NestedPathResolution.Empty;
        }

        var resolved = ImmutableArray.CreateBuilder<PlannedNestedInstallerFile>();
        foreach (PlannedNestedInstallerFile nested in previous.NestedInstallerFiles)
        {
            string[] exact = actualPaths
                .Where(path => string.Equals(path, nested.RelativeFilePath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string templatedPath = ReplaceVersion(nested.RelativeFilePath, previous.PackageVersion.Value, newVersion);
            string[] templated = actualPaths
                .Where(path => string.Equals(path, templatedPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string[] matches = exact.Concat(templated).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length != 1)
            {
                return NestedPathResolution.Unresolved(
                    matches.Length == 0 ? "NESTED_PATH_REMOVED" : "NESTED_PATH_AMBIGUOUS",
                    $"Could not uniquely re-derive nested path '{nested.RelativeFilePath}' from bounded archive contents.");
            }

            resolved.Add(new(matches[0], nested.PortableCommandAlias));
        }

        if (resolved.Select(static file => file.RelativeFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolved.Count
            || resolved
                .Select(static file => file.PortableCommandAlias)
                .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
                != resolved.Count(static file => !string.IsNullOrWhiteSpace(file.PortableCommandAlias)))
        {
            return NestedPathResolution.Unresolved(
                "NESTED_DUPLICATE",
                "Nested installer paths and non-empty aliases must be distinct.");
        }

        return new([.. resolved], null, null);
    }

    private static string ReplaceVersion(string path, string oldVersion, string newVersion)
    {
        if (string.IsNullOrEmpty(oldVersion))
        {
            return path;
        }

        string replaced = path.Replace(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase);
        string underscoredOld = oldVersion.Replace('.', '_');
        string underscoredNew = newVersion.Replace('.', '_');
        return replaced.Replace(underscoredOld, underscoredNew, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record NestedPathResolution(
    ImmutableArray<PlannedNestedInstallerFile> Files,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static NestedPathResolution Empty { get; } = new([], null, null);

    public static NestedPathResolution Unresolved(string code, string message) => new([], code, message);
}
