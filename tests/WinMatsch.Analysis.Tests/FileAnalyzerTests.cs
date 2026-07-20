using Xunit;

namespace WinMatsch.Analysis.Tests;

public class FileAnalyzerTests
{
    [Theory]
    [InlineData("app.zip", true)]
    [InlineData("app.exe", true)]
    [InlineData("app.EXE", true)]
    [InlineData("app.msi", false)]
    [InlineData("app.msix", false)]
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
    public void Unknown_extension_throws_not_supported()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => FileAnalyzer.Analyze(stream, "app.msi"));

        Assert.Contains(".msi", exception.Message, StringComparison.Ordinal);
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
