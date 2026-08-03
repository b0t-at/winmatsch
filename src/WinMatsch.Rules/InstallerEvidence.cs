using WinMatsch.Analysis;

namespace WinMatsch.Rules;

/// <summary>
/// The analysis evidence for one installer URL, as consumed by rules. Wraps the
/// <see cref="InstallerAnalysis"/> produced by WinMatsch.Analysis and adds a generic
/// string-keyed evidence bag for analyzer facts that are not (yet) modeled as typed
/// properties — for example the MSI summary-information <c>Comments</c> value used by the
/// Google Chrome quirk. Keys are compared ordinally and are the analyzer's own names
/// (e.g. <c>Comments</c>).
/// </summary>
public sealed class InstallerEvidence
{
    /// <summary>The installer URL this evidence belongs to; matched case-insensitively against <see cref="WinMatsch.Core.Installer.InstallerUrl"/>.</summary>
    public required string InstallerUrl { get; init; }

    /// <summary>The typed analysis result, when the installer was downloaded and analyzed.</summary>
    public InstallerAnalysis? Analysis { get; init; }

    /// <summary>Additional untyped evidence, keyed by analyzer-defined property names.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}
