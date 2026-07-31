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
    public void Single_msi_candidate_is_refined_through_the_msi_analyzer()
    {
        byte[] msi = MsiFixtures.BuildMsi(
            [("ProductName", "Contoso Editor"), ("Manufacturer", "Contoso Ltd"), ("ProductVersion", "2.4.1")],
            template: "x64;1033");
        using MemoryStream zip = BuildZip(("installers/app.msi", msi));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "app.zip");

        Assert.Equal(DetectedInstallerFormat.Zip, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Zip, installer.InstallerType);
        Assert.Equal(InstallerType.Msi, installer.NestedInstallerType);
        NestedInstallerFile nested = Assert.Single(installer.NestedInstallerFiles!);
        Assert.Equal("installers/app.msi", nested.RelativeFilePath);
        Assert.Equal(Architecture.X64, installer.Architecture); // From the nested MSI's Template.
        Assert.Equal("Contoso Editor", analysis.ProductName);
        Assert.Equal("Contoso Ltd", analysis.Publisher);
        Assert.Equal("2.4.1", analysis.ProductVersion);
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
        byte[] payload = Path.GetExtension(entryName).Contains("bundle", StringComparison.OrdinalIgnoreCase)
            ? MsixFixtures.BuildBundle("""
                <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
                  <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" />
                  <Packages><Package Type="application" Architecture="x64" /></Packages>
                </Bundle>
                """).ToArray()
            : MsixFixtures.BuildPackage(MsixFixtures.PackageManifest()).ToArray();
        using MemoryStream zip = BuildZip((entryName, payload));

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
            ("a/setup-x64.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)),
            ("b/setup-x86.msi", MsiFixtures.BuildMsi([])));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "multi.zip");

        Assert.Equal(DetectedInstallerFormat.Zip, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Zip, installer.InstallerType);
        Assert.Null(installer.NestedInstallerType);
        Assert.Null(installer.NestedInstallerFiles);
        Assert.NotNull(analysis.Zip);
        Assert.False(analysis.Zip.HasSingleCandidate);
        Assert.Equal(["a/setup-x64.exe", "b/setup-x86.msi"], analysis.Zip.NestedInstallerCandidates);
        Assert.True(Assert.Single(analysis.Diagnostics).RequiresManualAnalysis);
    }

    [Fact]
    public void Multiple_portable_binaries_for_one_architecture_are_grouped_with_distinct_aliases()
    {
        using MemoryStream zip = BuildZip(
            ("bin/tool.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)),
            ("bin/helper.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "tools.zip");

        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(InstallerType.Portable, installer.NestedInstallerType);
        List<NestedInstallerFile> nestedFiles = Assert.IsType<List<NestedInstallerFile>>(installer.NestedInstallerFiles);
        Assert.Equal(["tool", "helper"], nestedFiles.Select(static file => file.PortableCommandAlias));
        Assert.Equal(["bin/tool.exe", "bin/helper.exe"], nestedFiles.Select(static file => file.RelativeFilePath));
    }

    [Fact]
    public void Universal_portable_archive_produces_one_entry_per_architecture()
    {
        using MemoryStream zip = BuildZip(
            ("x86/tool.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.I386)),
            ("x64/tool.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)));

        InstallerAnalysis analysis = _analyzer.Analyze(zip, "universal.zip");

        Assert.Equal([Architecture.X86, Architecture.X64], analysis.Installers.Select(static installer => installer.Architecture));
        Assert.All(analysis.Installers, static installer => Assert.Equal(InstallerType.Portable, installer.NestedInstallerType));
        Assert.Contains(analysis.Diagnostics, static diagnostic => diagnostic.Code == "ZIP002");
    }

    [Fact]
    public void Duplicate_derived_portable_aliases_require_manual_selection()
    {
        using MemoryStream zip = BuildZip(
            ("a/tool.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)),
            ("b/tool.exe", PeFixtures.BuildExe(machine: System.Reflection.PortableExecutable.Machine.Amd64)));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "tools.zip"));

        Assert.Contains("duplicate command alias", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Macosx_and_resources_folders_are_skipped()
    {
        using MemoryStream zip = BuildZip(
            ("__MACOSX/app.exe", [1]),
            ("__macosx/other.msi", [2]),
            ("app/resources/helper.exe", [3]),
            ("payload/app.msi", MsiFixtures.BuildMsi([])));

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
            ("bin/app.msi", MsiFixtures.BuildMsi([])));

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
    public void Entry_with_drive_rooted_path_is_rejected_as_hostile()
    {
        using MemoryStream zip = BuildZip((@"C:\evil.exe", PeFixtures.BuildExe()));

        Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "evil.zip"));
    }

    [Fact]
    public void Excessive_path_depth_is_rejected()
    {
        string path = string.Join('/', Enumerable.Repeat("nested", AnalysisLimits.MaxArchivePathDepth + 1)) + "/app.exe";
        using MemoryStream zip = BuildZip((path, PeFixtures.BuildExe()));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "deep.zip"));

        Assert.Contains("path depth", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Excessive_entry_count_is_rejected_before_candidate_analysis()
    {
        (string Name, byte[]? Content)[] entries = Enumerable.Range(0, AnalysisLimits.MaxArchiveEntries + 1)
            .Select(static index => ($"docs/{index}.txt", (byte[]?)Array.Empty<byte>()))
            .ToArray();
        using MemoryStream zip = BuildZip(entries);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "many.zip"));

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AnalysisLimits.MaxArchiveEntries.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Installable_extension_with_wrong_magic_requires_manual_analysis()
    {
        using MemoryStream zip = BuildZip(("setup.exe", "not a PE"u8.ToArray()));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(zip, "bad.zip"));

        Assert.Contains("magic", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manual analysis is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_zip_disguised_as_nested_msix_is_not_misclassified()
    {
        using MemoryStream disguisedPackage = BuildZip(("setup.exe", PeFixtures.BuildExe()));
        using MemoryStream outer = BuildZip(("payload.msix", disguisedPackage.ToArray()));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _analyzer.Analyze(outer, "outer.zip"));

        Assert.Contains("required MSIX/AppX package manifest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Manual analysis is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Backslash_entry_names_are_normalized_to_forward_slashes()
    {
        using MemoryStream zip = BuildZip((@"bin\app.msi", MsiFixtures.BuildMsi([]))); ;

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
