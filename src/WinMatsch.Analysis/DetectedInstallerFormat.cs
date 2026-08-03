namespace WinMatsch.Analysis;

/// <summary>
/// The installer technology detected by analysis. More granular than the manifest's
/// <c>InstallerType</c>: it distinguishes, for example, an EXE that was positively identified
/// as a specific setup framework from one that merely looks like an installer.
/// </summary>
public enum DetectedInstallerFormat
{
    /// <summary>A Windows Installer package (.msi).</summary>
    Msi,

    /// <summary>An MSIX or AppX application package.</summary>
    Msix,

    /// <summary>An MSIX or AppX bundle containing per-architecture packages.</summary>
    MsixBundle,

    /// <summary>A plain archive carrying a nested installer or portable payload.</summary>
    Zip,

    /// <summary>An Inno Setup installer executable.</summary>
    InnoSetup,

    /// <summary>An NSIS (Nullsoft Scriptable Install System) installer executable.</summary>
    Nullsoft,

    /// <summary>A WiX Burn bundle executable that chains MSI/EXE packages.</summary>
    Burn,

    /// <summary>An Advanced Installer executable (7-Zip SFX wrapped).</summary>
    AdvancedInstaller,

    /// <summary>A Squirrel (or Clowd.Squirrel) installer executable.</summary>
    Squirrel,

    /// <summary>An EXE recognized as an installer only by generic keyword heuristics.</summary>
    GenericInstallerExe,

    /// <summary>A standalone executable that is run directly rather than installed.</summary>
    PortableExe,
}
