namespace WinMatsch.Core;

/// <summary>File format of a package icon.</summary>
public enum IconFileType
{
    Png,
    Jpeg,
    Ico,
}

/// <summary>Resolution of a package icon.</summary>
public enum IconResolution
{
    /// <summary>YAML value: <c>custom</c>.</summary>
    Custom,

    /// <summary>YAML value: <c>16</c> (16x16 pixels). Other fixed resolutions follow the same convention.</summary>
    Size16,
    Size20,
    Size24,
    Size30,
    Size32,
    Size36,
    Size40,
    Size48,
    Size60,
    Size64,
    Size72,
    Size80,
    Size96,
    Size256,
}

/// <summary>Theme a package icon is designed for.</summary>
public enum IconTheme
{
    Default,
    Light,
    Dark,
    HighContrast,
}
