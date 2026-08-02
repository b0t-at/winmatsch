using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Core.Tests;

public sealed class DurableFileSystemTests
{
    [Fact]
    public void ReplaceFile_preserves_same_filesystem_cross_directory_behavior()
    {
        string root = Directory.CreateTempSubdirectory("winmatsch-durable-replace-").FullName;
        try
        {
            string sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string destinationDirectory = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            string source = Path.Combine(sourceDirectory, "state.tmp");
            string destination = Path.Combine(destinationDirectory, "state.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            DurableFileSystem.ReplaceFile(source, destination);

            Assert.False(File.Exists(source));
            Assert.Equal("new", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    [Fact]
    public void CreateDirectoryDurably_creates_every_missing_path_component()
    {
        string root = Directory.CreateTempSubdirectory("winmatsch-durable-directory-").FullName;
        try
        {
            string target = Path.Combine(root, "state", "journals");

            DurableFileSystem.CreateDirectoryDurably(target);

            Assert.True(Directory.Exists(target));
            Assert.True(Directory.Exists(Path.Combine(root, "state")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
