namespace WinMatsch.Rules;

/// <summary>
/// The built-in per-package quirk data, keyed by package identifier (case-insensitive).
/// Kept as a hand-written dictionary for now; the shape is pure data so it can move to a
/// YAML file (or be merged with user-provided packs) later without touching any rule code.
/// </summary>
internal static class QuirkPack
{
    public static IReadOnlyDictionary<string, PackageQuirks> Quirks { get; } =
        new Dictionary<string, PackageQuirks>(StringComparer.OrdinalIgnoreCase)
        {
            // Chrome's MSI reports an internal build number (e.g. 66.x) as ProductVersion; the
            // real marketing version lives in the MSI summary-information Comments field.
            ["Google.Chrome"] = new() { DisplayVersionFromEvidenceProperty = "Comments" },
        };
}
