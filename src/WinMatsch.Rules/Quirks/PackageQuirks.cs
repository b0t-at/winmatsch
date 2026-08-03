namespace WinMatsch.Rules;

/// <summary>
/// The data-driven quirks for one package identifier. Quirks describe *what* is special about
/// a package; the generic <see cref="ApplyPackageQuirksRule"/> (WM0201) interprets them, so new
/// quirk data never needs new code paths per package.
/// </summary>
public sealed class PackageQuirks
{
    /// <summary>
    /// The name of an <see cref="InstallerEvidence.Properties"/> entry whose value is preferred
    /// as the AppsAndFeaturesEntries <c>DisplayVersion</c> of the matching installer. Example:
    /// Google Chrome's MSI stores its marketing version in the summary-information
    /// <c>Comments</c> field, not in <c>ProductVersion</c>.
    /// </summary>
    public string? DisplayVersionFromEvidenceProperty { get; init; }
}
