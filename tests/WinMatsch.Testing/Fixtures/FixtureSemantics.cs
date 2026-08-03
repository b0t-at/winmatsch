using WinMatsch.Core;

namespace WinMatsch.Testing.Fixtures;

public static class FixtureSemantics
{
    public static Architecture ParseArchitecture(string value)
        => value.ToLowerInvariant() switch
        {
            "x86" => Architecture.X86,
            "x64" => Architecture.X64,
            "arm" => Architecture.Arm,
            "arm64" => Architecture.Arm64,
            "neutral" => Architecture.Neutral,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown fixture architecture."),
        };

    public static InstallerType ParseInstallerType(string value)
        => value.ToLowerInvariant() switch
        {
            "appx" => InstallerType.Appx,
            "burn" => InstallerType.Burn,
            "exe" => InstallerType.Exe,
            "inno" => InstallerType.Inno,
            "msi" => InstallerType.Msi,
            "msix" => InstallerType.Msix,
            "nullsoft" => InstallerType.Nullsoft,
            "portable" => InstallerType.Portable,
            "wix" => InstallerType.Wix,
            "zip" => InstallerType.Zip,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown fixture installer type."),
        };

    public static Scope? ParseScope(string? value)
        => value?.ToLowerInvariant() switch
        {
            null => null,
            "user" => Scope.User,
            "machine" => Scope.Machine,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown fixture scope."),
        };
}
