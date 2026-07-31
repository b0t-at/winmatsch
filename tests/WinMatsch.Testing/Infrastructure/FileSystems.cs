namespace WinMatsch.Testing.Infrastructure;

public interface ITestFileSystem
{
    public bool FileExists(string path);

    public void CreateDirectory(string path);

    public Stream OpenRead(string path);

    public Stream CreateFile(string path);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    public void DeleteFile(string path);

    public byte[] ReadAllBytes(string path);

    public void WriteAllBytes(string path, byte[] contents);
}

public sealed class PhysicalTestFileSystem : ITestFileSystem
{
    public static PhysicalTestFileSystem Instance { get; } = new();

    private PhysicalTestFileSystem()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream CreateFile(string path) => File.Create(path);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);
}

public sealed class InMemoryFileSystem : ITestFileSystem
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Paths
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Keys.Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    public bool FileExists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(Normalize(path));
        }
    }

    public void CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
    }

    public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

    public Stream CreateFile(string path)
    {
        string normalizedPath = Normalize(path);
        return new CommittingMemoryStream(contents =>
        {
            lock (_gate)
            {
                _files[normalizedPath] = contents;
            }
        });
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        string source = Normalize(sourcePath);
        string destination = Normalize(destinationPath);

        lock (_gate)
        {
            if (!_files.TryGetValue(source, out byte[]? contents))
            {
                throw new FileNotFoundException("The source file does not exist.", sourcePath);
            }

            if (!overwrite && _files.ContainsKey(destination))
            {
                throw new IOException($"The destination file '{destinationPath}' already exists.");
            }

            _files[destination] = contents;
            _files.Remove(source);
        }
    }

    public void DeleteFile(string path)
    {
        lock (_gate)
        {
            _files.Remove(Normalize(path));
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_gate)
        {
            string normalizedPath = Normalize(path);
            return _files.TryGetValue(normalizedPath, out byte[]? contents)
                ? [.. contents]
                : throw new FileNotFoundException("The file does not exist.", path);
        }
    }

    public void WriteAllBytes(string path, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        lock (_gate)
        {
            _files[Normalize(path)] = [.. contents];
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).Replace('\\', '/');
    }

    private sealed class CommittingMemoryStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                commit(ToArray());
                _committed = true;
            }

            base.Dispose(disposing);
        }
    }
}
