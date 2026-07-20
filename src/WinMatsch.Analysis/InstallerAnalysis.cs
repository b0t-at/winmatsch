using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// The outcome of analyzing one installer file: the detected format, one or more proposed
/// installer entries, and display metadata harvested from the binary for later locale
/// enrichment. Fields that could not be determined are left null; validation rules and the
/// CLI fill the gaps (for example the architecture from URL tokens or user input).
/// </summary>
public sealed class InstallerAnalysis
{
    private readonly IReadOnlyList<Installer> _installers = [];

    /// <summary>The installer technology that was detected.</summary>
    public required DetectedInstallerFormat Format { get; init; }

    /// <summary>The proposed installer entries. Always contains at least one element.</summary>
    public required IReadOnlyList<Installer> Installers
    {
        get => _installers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0)
            {
                throw new ArgumentException("An analysis must produce at least one installer.", nameof(value));
            }

            _installers = value;
        }
    }

    /// <summary>The product name harvested from the binary (for example <c>ProductName</c> version string).</summary>
    public string? ProductName { get; init; }

    /// <summary>The publisher harvested from the binary (for example <c>CompanyName</c> version string).</summary>
    public string? Publisher { get; init; }

    /// <summary>The product version harvested from the binary (for example <c>ProductVersion</c> version string).</summary>
    public string? ProductVersion { get; init; }

    /// <summary>The copyright notice harvested from the binary (for example <c>LegalCopyright</c> version string).</summary>
    public string? Copyright { get; init; }

    /// <summary>What was found inside the archive; only set by archive analyzers.</summary>
    public ZipContents? Zip { get; init; }
}
