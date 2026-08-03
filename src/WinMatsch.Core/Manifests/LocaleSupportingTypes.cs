namespace WinMatsch.Core;

/// <summary>An agreement the user must accept to install the package.</summary>
public sealed class PackageAgreement
{
    public string? AgreementLabel { get; set; }

    public string? Agreement { get; set; }

    public string? AgreementUrl { get; set; }
}

/// <summary>A documentation link for the package.</summary>
public sealed class Documentation
{
    public string? DocumentLabel { get; set; }

    public string? DocumentUrl { get; set; }
}

/// <summary>A package icon.</summary>
public sealed class Icon
{
    public string? IconUrl { get; set; }

    public IconFileType? IconFileType { get; set; }

    public IconResolution? IconResolution { get; set; }

    public IconTheme? IconTheme { get; set; }

    public Sha256Hash? IconSha256 { get; set; }
}
