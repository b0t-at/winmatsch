using WinMatsch.Core;

namespace WinMatsch.Rules;

/// <summary>
/// The hand-written accessor table covering every <see cref="InstallerFieldsBase"/> property.
/// When a property is added to <see cref="InstallerFieldsBase"/> it must be added here as well;
/// a test compares this table against the type's properties to catch omissions.
/// </summary>
internal static class InstallerFieldAccessors
{
    /// <summary>All accessors, in the declaration order of <see cref="InstallerFieldsBase"/>.</summary>
    public static IReadOnlyList<InstallerFieldAccessor> All { get; } =
    [
        Scalar<LanguageTag>(nameof(InstallerFieldsBase.InstallerLocale), static f => f.InstallerLocale, static (f, v) => f.InstallerLocale = v),
        ValueList<Platform>(nameof(InstallerFieldsBase.Platform), static f => f.Platform, static (f, v) => f.Platform = v),
        Scalar<MinimumOSVersion>(nameof(InstallerFieldsBase.MinimumOSVersion), static f => f.MinimumOSVersion, static (f, v) => f.MinimumOSVersion = v),
        Value<InstallerType>(nameof(InstallerFieldsBase.InstallerType), static f => f.InstallerType, static (f, v) => f.InstallerType = v),
        Value<InstallerType>(nameof(InstallerFieldsBase.NestedInstallerType), static f => f.NestedInstallerType, static (f, v) => f.NestedInstallerType = v),
        ObjectList<NestedInstallerFile>(nameof(InstallerFieldsBase.NestedInstallerFiles), static f => f.NestedInstallerFiles, static (f, v) => f.NestedInstallerFiles = v, ManifestValues.NestedInstallerFileEqual, ManifestValues.CloneNestedInstallerFile),
        Value<Scope>(nameof(InstallerFieldsBase.Scope), static f => f.Scope, static (f, v) => f.Scope = v),
        ValueList<InstallMode>(nameof(InstallerFieldsBase.InstallModes), static f => f.InstallModes, static (f, v) => f.InstallModes = v),
        Complex<InstallerSwitches>(nameof(InstallerFieldsBase.InstallerSwitches), static f => f.InstallerSwitches, static (f, v) => f.InstallerSwitches = v, ManifestValues.SwitchesEqual, ManifestValues.CloneSwitches),
        ValueList<long>(nameof(InstallerFieldsBase.InstallerSuccessCodes), static f => f.InstallerSuccessCodes, static (f, v) => f.InstallerSuccessCodes = v),
        ObjectList<ExpectedReturnCode>(nameof(InstallerFieldsBase.ExpectedReturnCodes), static f => f.ExpectedReturnCodes, static (f, v) => f.ExpectedReturnCodes = v, ManifestValues.ExpectedReturnCodeEqual, ManifestValues.CloneExpectedReturnCode),
        Value<UpgradeBehavior>(nameof(InstallerFieldsBase.UpgradeBehavior), static f => f.UpgradeBehavior, static (f, v) => f.UpgradeBehavior = v),
        StringList(nameof(InstallerFieldsBase.Commands), static f => f.Commands, static (f, v) => f.Commands = v),
        StringList(nameof(InstallerFieldsBase.Protocols), static f => f.Protocols, static (f, v) => f.Protocols = v),
        StringList(nameof(InstallerFieldsBase.FileExtensions), static f => f.FileExtensions, static (f, v) => f.FileExtensions = v),
        Complex<Dependencies>(nameof(InstallerFieldsBase.Dependencies), static f => f.Dependencies, static (f, v) => f.Dependencies = v, ManifestValues.DependenciesEqual, ManifestValues.CloneDependencies),
        String(nameof(InstallerFieldsBase.PackageFamilyName), static f => f.PackageFamilyName, static (f, v) => f.PackageFamilyName = v),
        String(nameof(InstallerFieldsBase.ProductCode), static f => f.ProductCode, static (f, v) => f.ProductCode = v),
        StringList(nameof(InstallerFieldsBase.Capabilities), static f => f.Capabilities, static (f, v) => f.Capabilities = v),
        StringList(nameof(InstallerFieldsBase.RestrictedCapabilities), static f => f.RestrictedCapabilities, static (f, v) => f.RestrictedCapabilities = v),
        Complex<Markets>(nameof(InstallerFieldsBase.Markets), static f => f.Markets, static (f, v) => f.Markets = v, ManifestValues.MarketsEqual, ManifestValues.CloneMarkets),
        Value<bool>(nameof(InstallerFieldsBase.InstallerAbortsTerminal), static f => f.InstallerAbortsTerminal, static (f, v) => f.InstallerAbortsTerminal = v),
        Value<DateOnly>(nameof(InstallerFieldsBase.ReleaseDate), static f => f.ReleaseDate, static (f, v) => f.ReleaseDate = v),
        Value<bool>(nameof(InstallerFieldsBase.InstallLocationRequired), static f => f.InstallLocationRequired, static (f, v) => f.InstallLocationRequired = v),
        Value<bool>(nameof(InstallerFieldsBase.RequireExplicitUpgrade), static f => f.RequireExplicitUpgrade, static (f, v) => f.RequireExplicitUpgrade = v),
        Value<bool>(nameof(InstallerFieldsBase.DisplayInstallWarnings), static f => f.DisplayInstallWarnings, static (f, v) => f.DisplayInstallWarnings = v),
        ValueList<Architecture>(nameof(InstallerFieldsBase.UnsupportedOSArchitectures), static f => f.UnsupportedOSArchitectures, static (f, v) => f.UnsupportedOSArchitectures = v),
        ValueList<UnsupportedArgument>(nameof(InstallerFieldsBase.UnsupportedArguments), static f => f.UnsupportedArguments, static (f, v) => f.UnsupportedArguments = v),
        ObjectList<AppsAndFeaturesEntry>(nameof(InstallerFieldsBase.AppsAndFeaturesEntries), static f => f.AppsAndFeaturesEntries, static (f, v) => f.AppsAndFeaturesEntries = v, ManifestValues.AppsAndFeaturesEntryEqual, ManifestValues.CloneAppsAndFeaturesEntry),
        Value<ElevationRequirement>(nameof(InstallerFieldsBase.ElevationRequirement), static f => f.ElevationRequirement, static (f, v) => f.ElevationRequirement = v),
        Complex<InstallationMetadata>(nameof(InstallerFieldsBase.InstallationMetadata), static f => f.InstallationMetadata, static (f, v) => f.InstallationMetadata = v, ManifestValues.InstallationMetadataEqual, ManifestValues.CloneInstallationMetadata),
        Value<bool>(nameof(InstallerFieldsBase.DownloadCommandProhibited), static f => f.DownloadCommandProhibited, static (f, v) => f.DownloadCommandProhibited = v),
        Value<RepairBehavior>(nameof(InstallerFieldsBase.RepairBehavior), static f => f.RepairBehavior, static (f, v) => f.RepairBehavior = v),
        Value<bool>(nameof(InstallerFieldsBase.ArchiveBinariesDependOnPath), static f => f.ArchiveBinariesDependOnPath, static (f, v) => f.ArchiveBinariesDependOnPath = v),
        Complex<Authentication>(nameof(InstallerFieldsBase.Authentication), static f => f.Authentication, static (f, v) => f.Authentication = v, ManifestValues.AuthenticationEqual, ManifestValues.CloneAuthentication),
    ];

    /// <summary>An immutable reference-type scalar with value equality (shared on clone).</summary>
    private static InstallerFieldAccessor Scalar<T>(string name, Func<InstallerFieldsBase, T?> get, Action<InstallerFieldsBase, T?> set)
        where T : class => new()
        {
            Name = name,
            Get = f => get(f),
            Set = (f, v) => set(f, (T?)v),
            ValueEquals = static (a, b) => ((T)a).Equals((T)b),
            Clone = static v => v,
        };

    /// <summary>A string scalar (ordinal equality, shared on clone).</summary>
    private static InstallerFieldAccessor String(string name, Func<InstallerFieldsBase, string?> get, Action<InstallerFieldsBase, string?> set) => new()
    {
        Name = name,
        Get = f => get(f),
        Set = (f, v) => set(f, (string?)v),
        ValueEquals = static (a, b) => string.Equals((string)a, (string)b, StringComparison.Ordinal),
        Clone = static v => v,
    };

    /// <summary>A nullable value-type scalar (enum, bool, date).</summary>
    private static InstallerFieldAccessor Value<T>(string name, Func<InstallerFieldsBase, T?> get, Action<InstallerFieldsBase, T?> set)
        where T : struct => new()
        {
            Name = name,
            Get = f => get(f),
            Set = (f, v) => set(f, (T?)v),
            ValueEquals = static (a, b) => ((T)a).Equals((T)b),
            Clone = static v => v,
        };

    /// <summary>A list of value-type items (element-wise default equality, shallow-copied on clone).</summary>
    private static InstallerFieldAccessor ValueList<T>(string name, Func<InstallerFieldsBase, List<T>?> get, Action<InstallerFieldsBase, List<T>?> set)
        where T : struct => new()
        {
            Name = name,
            Get = f => get(f),
            Set = (f, v) => set(f, (List<T>?)v),
            ValueEquals = static (a, b) => ((List<T>)a).SequenceEqual((List<T>)b),
            Clone = static v => new List<T>((List<T>)v),
        };

    /// <summary>A list of strings (element-wise ordinal equality, shallow-copied on clone).</summary>
    private static InstallerFieldAccessor StringList(string name, Func<InstallerFieldsBase, List<string>?> get, Action<InstallerFieldsBase, List<string>?> set) => new()
    {
        Name = name,
        Get = f => get(f),
        Set = (f, v) => set(f, (List<string>?)v),
        ValueEquals = static (a, b) => ManifestValues.ListEqual((List<string>)a, (List<string>)b, ManifestValues.StringEqual),
        Clone = static v => new List<string>((List<string>)v),
    };

    /// <summary>A list of mutable composite items (element-wise deep equality, deep-cloned).</summary>
    private static InstallerFieldAccessor ObjectList<T>(string name, Func<InstallerFieldsBase, List<T>?> get, Action<InstallerFieldsBase, List<T>?> set, Func<T, T, bool> itemEqual, Func<T, T> itemClone)
        where T : class => new()
        {
            Name = name,
            Get = f => get(f),
            Set = (f, v) => set(f, (List<T>?)v),
            ValueEquals = (a, b) => ManifestValues.ListEqual((List<T>)a, (List<T>)b, itemEqual),
            Clone = v => ((List<T>)v).ConvertAll(x => itemClone(x)),
        };

    /// <summary>A mutable composite object (deep equality, deep-cloned).</summary>
    private static InstallerFieldAccessor Complex<T>(string name, Func<InstallerFieldsBase, T?> get, Action<InstallerFieldsBase, T?> set, Func<T, T, bool> equal, Func<T, T> clone)
        where T : class => new()
        {
            Name = name,
            Get = f => get(f),
            Set = (f, v) => set(f, (T?)v),
            ValueEquals = (a, b) => equal((T)a, (T)b),
            Clone = v => clone((T)v),
        };
}
