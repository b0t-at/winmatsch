namespace WinMatsch.Downloads.Tests;

/// <summary>
/// A read-only stream that serves a fixed prefix of bytes and then throws <see cref="IOException"/>,
/// simulating a connection dropped mid-download.
/// </summary>
internal sealed class FaultyStream(byte[] prefix) : Stream
{
    private readonly byte[] _prefix = prefix;
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _prefix.Length)
        {
            throw new IOException("Simulated connection drop.");
        }

        int read = Math.Min(count, _prefix.Length - _position);
        Array.Copy(_prefix, _position, buffer, offset, read);
        _position += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="IProgress{T}"/> that records reports synchronously on the reporting thread,
/// unlike <see cref="Progress{T}"/> which posts to a synchronization context.
/// </summary>
internal sealed class ProgressCollector : IProgress<DownloadProgress>
{
    private readonly Lock _gate = new();
    private readonly List<DownloadProgress> _reports = [];

    public IReadOnlyList<DownloadProgress> Reports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (_gate)
        {
            _reports.Add(value);
        }
    }
}
