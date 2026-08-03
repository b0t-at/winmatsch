using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;

namespace WinMatsch.Workflows.Mapping;

internal static class NestedInstallerPathResolver
{
    public static NestedPathResolution Resolve(
        PreviousInstallerEntry? previous,
        AssetAnalysisEvidence? analysis,
        AnalyzedInstallerShape? shape,
        string newVersion)
    {
        if (analysis is null)
        {
            return previous is { NestedInstallerFiles.IsEmpty: false }
                ? NestedPathResolution.Unresolved("NESTED_REANALYSIS_REQUIRED", "Archive contents are unavailable.")
                : NestedPathResolution.Empty;
        }

        if (analysis.Format != DetectedInstallerFormat.Zip
            && shape?.InstallerType != InstallerType.Zip)
        {
            return NestedPathResolution.Empty;
        }

        string[] actualPaths = analysis.ArchiveEntries
            .GroupBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Order(StringComparer.Ordinal).First())
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actualPaths.Length == 0)
        {
            return analysis.Format == DetectedInstallerFormat.Zip
                || previous is { NestedInstallerFiles.IsEmpty: false }
                ? NestedPathResolution.Unresolved(
                    "NESTED_BOUNDED_CONTENTS_REQUIRED",
                    "Nested installer paths require the bounded archive entry set.")
                : NestedPathResolution.Empty;
        }

        if (previous is null || previous.NestedInstallerFiles.IsEmpty)
        {
            ImmutableArray<PlannedNestedInstallerFile> analyzedFiles =
                shape?.NestedInstallerFiles ?? [];
            if (!analyzedFiles.IsEmpty)
            {
                return ValidateAnalyzedFiles(analyzedFiles, actualPaths);
            }

            if (analysis.NestedInstallerCandidates.Length == 1)
            {
                return new(
                    [new PlannedNestedInstallerFile(analysis.NestedInstallerCandidates[0], null)],
                    null,
                    null);
            }

            return analysis.NestedInstallerCandidates.Length > 1
                ? NestedPathResolution.Unresolved(
                    "NESTED_PATH_AMBIGUOUS",
                    "Multiple nested installer candidates require an analyzer-selected file set or explicit input.")
                : NestedPathResolution.Unresolved(
                    "NESTED_PATH_UNRESOLVED",
                    "No nested installer file was selected from the bounded archive contents.");
        }

        var resolved = ImmutableArray.CreateBuilder<PlannedNestedInstallerFile>();
        foreach (PlannedNestedInstallerFile nested in previous.NestedInstallerFiles
                     .OrderBy(static file => file.RelativeFilePath, StringComparer.Ordinal)
                     .ThenBy(static file => file.PortableCommandAlias, StringComparer.Ordinal))
        {
            string[] exact = actualPaths
                .Where(path => string.Equals(path, nested.RelativeFilePath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string[] templatedPaths = GenerateVersionTemplates(
                nested.RelativeFilePath,
                previous.PackageVersion.Value,
                newVersion);
            string[] templated = actualPaths
                .Where(path => templatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            string[] matches = exact
                .Concat(templated)
                .GroupBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.Order(StringComparer.Ordinal).First())
                .Order(StringComparer.Ordinal)
                .ToArray();
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

    private static NestedPathResolution ValidateAnalyzedFiles(
        ImmutableArray<PlannedNestedInstallerFile> files,
        IReadOnlyCollection<string> actualPaths)
    {
        if (files.Any(file => !actualPaths.Contains(file.RelativeFilePath, StringComparer.OrdinalIgnoreCase)))
        {
            return NestedPathResolution.Unresolved(
                "NESTED_PATH_REMOVED",
                "An analyzer-selected nested installer path is absent from bounded archive contents.");
        }

        if (files.Select(static file => file.RelativeFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length
            || files
                .Select(static file => file.PortableCommandAlias)
                .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
                != files.Count(static file => !string.IsNullOrWhiteSpace(file.PortableCommandAlias)))
        {
            return NestedPathResolution.Unresolved(
                "NESTED_DUPLICATE",
                "Nested installer paths and non-empty aliases must be distinct.");
        }

        return new(
            [
                .. files
                    .OrderBy(static file => file.RelativeFilePath, StringComparer.Ordinal)
                    .ThenBy(static file => file.PortableCommandAlias, StringComparer.Ordinal),
            ],
            null,
            null);
    }

    private static string[] GenerateVersionTemplates(string path, string oldVersion, string newVersion)
    {
        if (string.IsNullOrEmpty(oldVersion))
        {
            return [];
        }

        (string Old, string New)[] representations =
        [
            (oldVersion, newVersion),
            (oldVersion.Replace('.', '_'), newVersion.Replace('.', '_')),
            (oldVersion.Replace('.', '-'), newVersion.Replace('.', '-')),
        ];
        var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string oldToken, string newToken) in representations)
        {
            string? allReplaced = ReplaceAllBounded(path, oldToken, newToken);
            if (allReplaced is not null)
            {
                templates.Add(allReplaced);
            }

            int index = path.IndexOf(oldToken, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int end = index + oldToken.Length;
                bool boundedBefore = index == 0 || !char.IsAsciiLetterOrDigit(path[index - 1]);
                bool boundedAfter = end == path.Length || !char.IsAsciiLetterOrDigit(path[end]);
                if (boundedBefore && boundedAfter)
                {
                    templates.Add(string.Concat(path.AsSpan(0, index), newToken.AsSpan(), path.AsSpan(end)));
                }

                index = path.IndexOf(oldToken, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return [.. templates.Order(StringComparer.Ordinal)];
    }

    private static string? ReplaceAllBounded(string path, string oldToken, string newToken)
    {
        var result = new System.Text.StringBuilder(path.Length);
        int copiedUntil = 0;
        int replacements = 0;
        int index = path.IndexOf(oldToken, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + oldToken.Length;
            bool boundedBefore = index == 0 || !char.IsAsciiLetterOrDigit(path[index - 1]);
            bool boundedAfter = end == path.Length || !char.IsAsciiLetterOrDigit(path[end]);
            if (boundedBefore && boundedAfter)
            {
                result.Append(path, copiedUntil, index - copiedUntil);
                result.Append(newToken);
                copiedUntil = end;
                replacements++;
            }

            index = path.IndexOf(oldToken, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        if (replacements == 0)
        {
            return null;
        }

        result.Append(path, copiedUntil, path.Length - copiedUntil);
        return result.ToString();
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
