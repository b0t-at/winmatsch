using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class DurableFileSystemTests
{
    [Fact]
    public void MoveFile_rejects_cross_directory_moves_before_mutating_source()
    {
        string root = Directory.CreateTempSubdirectory("winmatsch-durable-move-").FullName;
        try
        {
            string sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string destinationDirectory = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            string source = Path.Combine(sourceDirectory, "state.tmp");
            string destination = Path.Combine(destinationDirectory, "state.json");
            File.WriteAllText(source, "durable");

            IOException exception = Assert.Throws<IOException>(() =>
                DurableFileSystem.MoveFile(source, destination));

            Assert.Contains("same directory", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
