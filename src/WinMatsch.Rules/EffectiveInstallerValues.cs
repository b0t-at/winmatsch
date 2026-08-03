using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// Resolves the effective value of an installer field: the per-installer value when set,
/// otherwise the installer-manifest root default. Rules that need an installer's identity
/// (its uniqueness key) or behavior must look through the root defaults, because rules run
/// both before and after hoisting.
/// </summary>
internal static class EffectiveInstallerValues
{
    public static InstallerType? GetInstallerType(InstallerManifest manifest, Installer installer) =>
        installer.InstallerType ?? manifest.InstallerType;

    public static InstallerType? GetNestedInstallerType(InstallerManifest manifest, Installer installer) =>
        installer.NestedInstallerType ?? manifest.NestedInstallerType;

    public static List<NestedInstallerFile>? GetNestedInstallerFiles(InstallerManifest manifest, Installer installer) =>
        installer.NestedInstallerFiles ?? manifest.NestedInstallerFiles;

    public static Scope? GetScope(InstallerManifest manifest, Installer installer) =>
        installer.Scope ?? manifest.Scope;

    public static InstallerSwitches? GetInstallerSwitches(InstallerManifest manifest, Installer installer) =>
        installer.InstallerSwitches ?? manifest.InstallerSwitches;

    public static Dependencies? GetDependencies(InstallerManifest manifest, Installer installer) =>
        installer.Dependencies ?? manifest.Dependencies;

    public static List<AppsAndFeaturesEntry>? GetAppsAndFeaturesEntries(InstallerManifest manifest, Installer installer) =>
        installer.AppsAndFeaturesEntries ?? manifest.AppsAndFeaturesEntries;

    /// <summary>The Architecture+InstallerType+Scope part of an installer's uniqueness key.</summary>
    public static string GetEntryKey(InstallerManifest manifest, Installer installer) =>
        $"{Format(installer.Architecture)}|{Format(GetInstallerType(manifest, installer))}|{Format(GetScope(manifest, installer))}";

    private static string Format<T>(T? value)
        where T : struct, Enum => value?.ToString() ?? string.Empty;
}
