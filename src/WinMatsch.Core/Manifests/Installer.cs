namespace WinMatsch.Core;

/// <summary>A single installer entry in an installer manifest.</summary>
public sealed class Installer : InstallerFieldsBase
{
    /// <summary>The installer's target architecture. Required by the schema; nullable in the model so rules can diagnose rather than the parser failing.</summary>
    public Architecture? Architecture { get; set; }

    public string? InstallerUrl { get; set; }

    public Sha256Hash? InstallerSha256 { get; set; }

    /// <summary>SHA-256 of the signature file inside an MSIX/AppX package.</summary>
    public Sha256Hash? SignatureSha256 { get; set; }
}
