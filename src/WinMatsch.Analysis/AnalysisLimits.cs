using System.IO.Compression;
using System.Threading;

namespace WinMatsch.Analysis;

/// <summary>Resource ceilings shared by archive-backed analyzers.</summary>
internal static class AnalysisLimits
{
    public const int MaxArchiveEntries = 10_000;
    public const int MaxArchivePathDepth = 64;
    public const int MaxArchivePathLength = 2_048;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxExpandedArchiveBytes = 1024L * 1024 * 1024;
    public const int MaxNestedArchives = 4;
    public const int MaxPeSections = 96;
    public const int MaxResourceBytes = 16 * 1024 * 1024;
    public const int MaxMsiStreamBytes = 64 * 1024 * 1024;
    public const int MaxNsisHeaderBytes = 64 * 1024 * 1024;

    private static readonly AsyncLocal<int> _archiveDepth = new();

    public static IDisposable EnterArchive(string description)
    {
        int depth = _archiveDepth.Value + 1;
        if (depth > MaxNestedArchives)
        {
            throw new InvalidDataException(
                $"{description} exceeds the supported nesting limit of {MaxNestedArchives} archives. Manual analysis is required.");
        }

        _archiveDepth.Value = depth;
        return new ArchiveScope();
    }

    public static void ValidateArchive(ZipArchive archive, string description)
    {
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"{description} contains {archive.Entries.Count} entries; the analysis limit is {MaxArchiveEntries}.");
        }

        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateAllocation(entry.Length, $"{description} entry '{entry.FullName}'", MaxEntryBytes);
            try
            {
                total = checked(total + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException($"{description} declares an overflowing expanded size.", exception);
            }
        }

        ValidateExpandedSize(total, description);
    }

    public static byte[] ReadEntryBytes(ZipArchiveEntry entry, string description)
    {
        ValidateAllocation(entry.Length, description, MaxEntryBytes);
        byte[] bytes = new byte[checked((int)entry.Length)];
        using Stream stream = entry.Open();
        try
        {
            stream.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"{description} ends before its declared size.", exception);
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"{description} expands beyond its declared size.");
        }

        return bytes;
    }

    public static void ValidateAllocation(long size, string description, long maximum)
    {
        if (size < 0 || size > maximum || size > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{description} declares {size} bytes; the analysis allocation limit is {Math.Min(maximum, int.MaxValue)} bytes.");
        }
    }

    public static void ValidateExpandedSize(long size, string description)
    {
        if (size < 0 || size > MaxExpandedArchiveBytes)
        {
            throw new InvalidDataException(
                $"{description} expands to {size} bytes; the analysis limit is {MaxExpandedArchiveBytes} bytes.");
        }
    }

    private sealed class ArchiveScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _archiveDepth.Value--;
                _disposed = true;
            }
        }
    }
}
