namespace WinMatsch.Core;

/// <summary>The type of a WinGet manifest file.</summary>
public enum ManifestType
{
    /// <summary>YAML value: <c>version</c>.</summary>
    Version,

    /// <summary>YAML value: <c>installer</c>.</summary>
    Installer,

    /// <summary>YAML value: <c>defaultLocale</c>.</summary>
    DefaultLocale,

    /// <summary>YAML value: <c>locale</c>.</summary>
    Locale,

    /// <summary>YAML value: <c>singleton</c>. Legacy single-file manifests; read-only support.</summary>
    Singleton,
}
