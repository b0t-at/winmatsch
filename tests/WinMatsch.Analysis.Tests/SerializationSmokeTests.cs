using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using Xunit;

namespace WinMatsch.Analysis.Tests;

/// <summary>
/// End-to-end smoke test: an installer produced by analysis, completed with the fields only
/// the download step knows, serializes through the core YAML writer.
/// </summary>
public class SerializationSmokeTests
{
    [Fact]
    public void Analyzed_exe_installer_serializes_via_the_manifest_writer()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            ProductName: "Foo",
            CompanyName: "Foo Corp",
            ProductVersion: "1.2.3",
            FileDescription: "Foo Setup"));
        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "FooSetup.exe");

        Installer installer = analysis.Installers[0];
        installer.InstallerUrl = "https://example.com/FooSetup.exe";
        installer.InstallerSha256 = new Sha256Hash(new string('A', 64));

        var manifest = new InstallerManifest
        {
            PackageIdentifier = new PackageIdentifier("Foo.Foo"),
            PackageVersion = new PackageVersion("1.2.3"),
            Installers = [.. analysis.Installers],
        };

        string yaml = ManifestYamlWriter.Serialize(manifest);

        Assert.Contains("PackageIdentifier: Foo.Foo", yaml, StringComparison.Ordinal);
        Assert.Contains("Architecture: x64", yaml, StringComparison.Ordinal);
        Assert.Contains("InstallerType: exe", yaml, StringComparison.Ordinal);
        Assert.Contains("InstallerUrl: https://example.com/FooSetup.exe", yaml, StringComparison.Ordinal);
        Assert.Contains("InstallerSha256:", yaml, StringComparison.Ordinal);
    }
}
