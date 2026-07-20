using System.IO.Compression;
using System.Text;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class ZipAnalyzerTests
{
    private readonly ZipAnalyzer _analyzer = new();

    [Theory]
    [InlineData("app.zip", true)]
    [InlineData("APP.ZIP", true)]
    [InlineData("app.exe", false)]
    [InlineData("app.7z", false)]
    public void CanAnalyze_checks_the_extension_case_insensitively(string fileName, bool expected)
        => Assert.Equal(expected, _analyzer.CanAnalyze(fileName));

    [Fact]
    public void Single_msi_candidate_is_selected_with_relative_path()
    {
        using MemoryStream zip = BuildZip(("installers/app.msi", [1, 2, 3]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.Equal(DetectedInstallerFormat.Zip, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Zip, installer.InstallerType);
        Assert.Equal(InstallerType.Msi, installer.NestedInstallerType);
        NestedInstallerFile nested = Assert.Single(installer.NestedInstallerFiles!);
        Assert.Equal("installers/app.msi", nested.RelativeFilePath);
        Assert.Null(installer.Architecture); // No MSI analyzer registered yet; rules fill it later.
        Assert.NotNull(analysis.Zip);
        Assert.True(analysis.Zip.HasSingleCandidate);
    }

    [Theory]
    [InlineData("app.msixbundle", InstallerType.Msix)]
    [InlineData("app.MSIX", InstallerType.Msix)]
    [InlineData("app.appx", InstallerType.Appx)]
    [InlineData("app.appxbundle", InstallerType.Appx)]
    public void Nested_installer_type_is_mapped_from_the_extension(string entryName, InstallerType expected)
    {
        using MemoryStream zip = BuildZip((entryName, [1, 2, 3]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.Equal(expected, Assert.Single(analysis.Installers).NestedInstallerType);
    }

    [Fact]
    public void Single_nested_portable_exe_is_refined_via_the_inner_pe()
    {
        byte[] portableExe = PeFixtures.BuildExe(
            machine: System.Reflection.PortableExecutable.Machine.Amd64,
            version: new VersionStrings(
                ProductName: "Tool",
                CompanyName: "Tool Corp",
                ProductVersion: "9.9",
                OriginalFilename: "tool.exe",
                FileDescription: "A portable tool"));
        using MemoryStream zip = BuildZip(("bin/tool.exe", portableExe));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "tool.zip");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Zip, installer.InstallerType);
        Assert.Equal(InstallerType.Portable, installer.NestedInstallerType);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal("bin/tool.exe", Assert.Single(installer.NestedInstallerFiles!).RelativeFilePath);
        Assert.Equal("Tool", analysis.ProductName);
        Assert.Equal("Tool Corp", analysis.Publisher);
        Assert.Equal("9.9", analysis.ProductVersion);
    }

    [Fact]
    public void Single_nested_installer_exe_keeps_nested_type_exe()
    {
        byte[] installerExe = PeFixtures.BuildExe(
            machine: System.Reflection.PortableExecutable.Machine.Arm64,
            version: new VersionStrings(FileDescription: "Foo Setup"));
        using MemoryStream zip = BuildZip(("FooSetup.exe", installerExe));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "foo.zip");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Exe, installer.NestedInstallerType);
        Assert.Equal(Architecture.Arm64, installer.Architecture);
    }

    [Fact]
    public void Multiple_candidates_are_reported_without_choosing_one()
    {
        using MemoryStream zip = BuildZip(
            ("a/setup-x64.exe", [1]),
            ("b/setup-x86.msi", [2]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "multi.zip");

        Assert.Equal(DetectedInstallerFormat.Zip, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Zip, installer.InstallerType);
        Assert.Null(installer.NestedInstallerType);
        Assert.Null(installer.NestedInstallerFiles);
        Assert.NotNull(analysis.Zip);
        Assert.False(analysis.Zip.HasSingleCandidate);
        Assert.Equal(["a/setup-x64.exe", "b/setup-x86.msi"], analysis.Zip.NestedInstallerCandidates);
    }

    [Fact]
    public void Macosx_and_resources_folders_are_skipped()
    {
        using MemoryStream zip = BuildZip(
            ("__MACOSX/app.exe", [1]),
            ("__macosx/other.msi", [2]),
            ("app/resources/helper.exe", [3]),
            ("payload/app.msi", [4]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Msi, installer.NestedInstallerType);
        Assert.Equal("payload/app.msi", Assert.Single(installer.NestedInstallerFiles!).RelativeFilePath);
    }

    [Fact]
    public void A_file_named_resources_is_not_skipped()
    {
        using MemoryStream zip = BuildZip(("resources.exe", PeFixtures.BuildExe()));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.True(analysis.Zip!.HasSingleCandidate);
    }

    [Fact]
    public void Directory_entries_are_ignored()
    {
        using MemoryStream zip = BuildZip(
            ("bin/", null),
            ("bin/app.msi", [1]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.Equal("bin/app.msi", Assert.Single(analysis.Zip!.NestedInstallerCandidates));
    }

    [Fact]
    public void Archive_without_installable_payloads_throws()
    {
        using MemoryStream zip = BuildZip(("readme.txt", Encoding.UTF8.GetBytes("hello")));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "docs.zip"));

        Assert.Contains("no installable payloads", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_with_dotdot_segment_is_rejected_as_hostile()
    {
        using MemoryStream zip = BuildZip(("../evil.exe", [1]));

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "evil.zip"));
    }

    [Fact]
    public void Entry_with_absolute_path_is_rejected_as_hostile()
    {
        using MemoryStream zip = BuildZip(("/evil.exe", [1]));

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "evil.zip"));
    }

    [Fact]
    public void Backslash_entry_names_are_normalized_to_forward_slashes()
    {
        using MemoryStream zip = BuildZip((@"bin\app.msi", [1]));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.Equal("bin/app.msi", Assert.Single(analysis.Zip!.NestedInstallerCandidates));
    }

    /// <summary>Builds an in-memory zip; a null content marks a directory entry.</summary>
    private static MemoryStream BuildZip(params (string Name, byte[]? Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[]? content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                if (content is not null)
                {
                    using Stream entryStream = entry.Open();
                    entryStream.Write(content);
                }
            }
        }

        stream.Position = 0;
        return stream;
    }
}
