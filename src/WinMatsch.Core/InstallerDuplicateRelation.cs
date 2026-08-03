namespace WinMatsch.Core;

/// <summary>
/// Implements WinGet's duplicate-installer relation. Scope and locale are wildcard dimensions
/// when their effective value is absent, so this relation is symmetric but intentionally not
/// transitive: user matches unknown and unknown matches machine, while user does not match
/// machine. Callers must compare installer pairs instead of grouping by an equality key.
/// </summary>
public static class InstallerDuplicateRelation
{
    public static bool AreDuplicates(
        InstallerManifest manifest,
        Installer left,
        Installer right)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        EffectiveInstallerIdentity leftIdentity = GetIdentity(manifest, left);
        EffectiveInstallerIdentity rightIdentity = GetIdentity(manifest, right);
        return leftIdentity.Architecture == rightIdentity.Architecture
            && leftIdentity.InstallerType == rightIdentity.InstallerType
            && leftIdentity.NestedInstallerType == rightIdentity.NestedInstallerType
            && WildcardEquals(leftIdentity.Scope, rightIdentity.Scope)
            && WildcardEquals(leftIdentity.Locale, rightIdentity.Locale);
    }

    public static EffectiveInstallerIdentity GetIdentity(
        InstallerManifest manifest,
        Installer installer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installer);
        return new EffectiveInstallerIdentity(
            installer.Architecture,
            installer.InstallerType ?? manifest.InstallerType,
            installer.NestedInstallerType ?? manifest.NestedInstallerType,
            installer.Scope ?? manifest.Scope,
            installer.InstallerLocale ?? manifest.InstallerLocale);
    }

    private static bool WildcardEquals<T>(T? left, T? right)
        where T : struct
        => left is null || right is null || EqualityComparer<T>.Default.Equals(left.Value, right.Value);

    private static bool WildcardEquals(LanguageTag? left, LanguageTag? right)
        => left is null || right is null || left == right;
}

public readonly record struct EffectiveInstallerIdentity(
    Architecture? Architecture,
    InstallerType? InstallerType,
    InstallerType? NestedInstallerType,
    Scope? Scope,
    LanguageTag? Locale);
