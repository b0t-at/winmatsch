using System.Reflection.PortableExecutable;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// Builds Advanced Installer-style setup executables for probe tests: a PE stub
/// (<see cref="PeFixtures"/>) with a 7z archive (<see cref="SevenZipFixtures"/>) appended as
/// overlay, the archive wrapping an MSI produced by <see cref="MsiFixtures"/> — the layout
/// Advanced Installer's 7-Zip SFX bootstrapper uses.
/// </summary>
internal static class AdvancedInstallerFixtures
{
    /// <summary>Version strings resembling the branded Advanced Installer stub.</summary>
    public static VersionStrings BrandedStub { get; } = new(
        ProductName: "Contoso Studio",
        CompanyName: "Contoso Ltd",
        ProductVersion: "3.1.0",
        FileDescription: "This installation was built with Advanced Installer");

    /// <summary>Appends <paramref name="overlay"/> to a PE stub built with the given options.</summary>
    public static byte[] BuildSfx(
        byte[] overlay,
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null)
        => Concat(PeFixtures.BuildExe(machine, version, manifestXml), overlay);

    /// <summary>
    /// Builds a complete Advanced Installer-style setup: stub + 7z overlay wrapping a single
    /// MSI entry with the given property table and summary-information template.
    /// </summary>
    public static byte[] BuildInstaller(
        (string Name, string Value)[] msiProperties,
        string? template = "x64;1033",
        Machine machine = Machine.I386,
        VersionStrings? version = null,
        string? manifestXml = null,
        string msiEntryName = "product.msi",
        string? creatingApplication = "WiX Toolset v4")
        => BuildSfx(
            SevenZipFixtures.Build((msiEntryName, MsiFixtures.BuildMsi(msiProperties, template, creatingApplication))),
            machine,
            version,
            manifestXml);

    /// <summary>Concatenates byte blocks (stub + overlay payloads).</summary>
    public static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
