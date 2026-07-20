using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// Seam for JSON-schema validation of the produced manifests against the pinned WinGet
/// manifest schemas checked in under <c>schemas/</c> (currently 1.10.0).
/// No implementation ships yet: the maintained JSON-schema packages either pull in
/// reflection-heavy dependencies or need vetting for AOT compatibility, and the rule
/// pipeline's findings already cover the rule half of <c>validate</c>. An implementation
/// plugs in here without touching the pipeline: run it after the pipeline and merge its
/// findings (use a reserved rule id such as <c>WM0100</c>).
/// </summary>
public interface IManifestSchemaValidator
{
    /// <summary>Validates all four manifests and returns schema findings; empty when everything conforms.</summary>
    public IReadOnlyList<RuleFinding> Validate(PackageManifests manifests);
}
