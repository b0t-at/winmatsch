using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class FileAnalyzerTests
{
    [Theory]
    [InlineData("app.zip", true)]
    [InlineData("app.exe", true)]
    [InlineData("app.EXE", true)]
    [InlineData("app.msi", true)]
    [InlineData("app.msix", true)]
    [InlineData("app.appx", true)]
    [InlineData("app.msixbundle", true)]
    [InlineData("app.appxbundle", true)]
    [InlineData("app.7z", false)]
    [InlineData("app.txt", false)]
    public void CanAnalyze_reflects_the_registered_analyzers(string fileName, bool expected)
        => Assert.Equal(expected, FileAnalyzer.CanAnalyze(fileName));

    [Fact]
    public void Analyze_dispatches_to_the_exe_analyzer()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(FileDescription: "Foo Setup"));

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "FooSetup.exe");

        Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
    }

    [Fact]
    public void Analyze_dispatches_specialized_exe_content_through_the_production_registry()
    {
        using var stream = new MemoryStream(InnoFixtures.BuildInstaller());

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "setup.exe");

        Assert.Equal(DetectedInstallerFormat.InnoSetup, analysis.Format);
    }

    [Fact]
    public void Analyze_dispatches_to_the_msi_analyzer()
    {
        using var stream = new MemoryStream(MsiFixtures.BuildMsi([("ProductName", "Contoso")]));

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "contoso.msi");

        Assert.Equal(DetectedInstallerFormat.Msi, analysis.Format);
    }

    [Fact]
    public void Analyze_dispatches_to_the_msix_analyzer()
    {
        using MemoryStream stream = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest());

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "app.msix");

        Assert.Equal(DetectedInstallerFormat.Msix, analysis.Format);
    }

    [Fact]
    public void Packaging_change_from_exe_to_zip_is_reanalyzed_from_magic()
    {
        byte[] msi = MsiFixtures.BuildMsi([("ProductName", "Contoso")]);
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
            stream,
            System.IO.Compression.ZipArchiveMode.Create,
            leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("payload/app.msi");
            using Stream entryStream = entry.Open();
            entryStream.Write(msi);
        }

        stream.Position = 0;
        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "previously-an-exe.exe");

        Assert.Equal(DetectedInstallerFormat.Zip, analysis.Format);
        Assert.Equal(InstallerType.Zip, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Packaging_change_from_zip_to_exe_is_reanalyzed_from_magic()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "previously-a-zip.zip");

        Assert.Equal(DetectedInstallerFormat.PortableExe, analysis.Format);
        Assert.Equal(InstallerType.Portable, Assert.Single(analysis.Installers).InstallerType);
    }

    [Fact]
    public void Msix_content_renamed_to_zip_is_still_analyzed_as_msix()
    {
        using MemoryStream stream = MsixFixtures.BuildPackage(MsixFixtures.PackageManifest());

        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "renamed.zip");

        Assert.Equal(DetectedInstallerFormat.Msix, analysis.Format);
    }

    [Fact]
    public void Known_extension_with_unrecognized_magic_requires_manual_analysis()
    {
        using var stream = new MemoryStream("not an installer"u8.ToArray());

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => FileAnalyzer.Analyze(stream, "fake.msi"));

        Assert.Contains("Manual analysis is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_extension_throws_not_supported()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => FileAnalyzer.Analyze(stream, "app.7z"));

        Assert.Contains(".7z", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_extension_with_supported_content_still_throws_not_supported()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => FileAnalyzer.Analyze(stream, "renamed.bin"));

        Assert.Contains(".bin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_seekable_stream_is_rejected()
    {
        using var stream = new NonSeekableStream();

        Assert.Throws<ArgumentException>(() => FileAnalyzer.Analyze(stream, "app.exe"));
    }

    [Fact]
    public void AnalyzeFile_opens_the_file_and_dispatches_by_its_name()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winmatsch-test-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, PeFixtures.BuildExe(version: new VersionStrings(FileDescription: "Foo Setup")));

            InstallerAnalysis analysis = FileAnalyzer.AnalyzeFile(path);

            Assert.Equal(DetectedInstallerFormat.GenericInstallerExe, analysis.Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
