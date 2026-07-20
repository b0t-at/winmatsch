namespace WinMatsch.Analysis;

/// <summary>
/// What an archive analysis found inside a .zip: the relative paths of all entries that look
/// like installable payloads. When more than one candidate exists, the interactive flow uses
/// this list to prompt the user for a choice.
/// </summary>
public sealed class ZipContents
{
    public ZipContents(IReadOnlyList<string> nestedInstallerCandidates)
    {
        ArgumentNullException.ThrowIfNull(nestedInstallerCandidates);
        NestedInstallerCandidates = nestedInstallerCandidates;
    }

    /// <summary>Relative paths of nested installer candidates inside the archive, using forward slashes.</summary>
    public IReadOnlyList<string> NestedInstallerCandidates { get; }

    /// <summary>Whether exactly one candidate was found, so no user prompt is needed.</summary>
    public bool HasSingleCandidate => NestedInstallerCandidates.Count == 1;
}
