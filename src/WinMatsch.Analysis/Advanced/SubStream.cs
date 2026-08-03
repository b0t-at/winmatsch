namespace WinMatsch.Analysis.Advanced;

/// <summary>
/// A read-only, seekable view over a slice of an underlying stream.
/// </summary>
/// <remarks>
/// Archive readers expect a stream whose position <c>0</c> is the start of the
/// archive. Installer payloads are appended to the executable as overlay data,
/// so this type re-bases an offset within the installer stream to position
/// <c>0</c> without copying the payload. Disposing a <see cref="SubStream"/>
/// never disposes the underlying stream; the caller retains ownership.
/// </remarks>
internal sealed class SubStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    /// <summary>Creates a view over <paramref name="baseStream"/> starting at <paramref name="offset"/>.</summary>
    /// <param name="baseStream">The seekable stream to wrap. Not disposed by this instance.</param>
    /// <param name="offset">Absolute offset in <paramref name="baseStream"/> that becomes position 0.</param>
    /// <param name="length">Number of bytes exposed by the view.</param>
    public SubStream(Stream baseStream, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _baseStream = baseStream;
        _offset = offset;
        _length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, remaining);
        _baseStream.Position = _offset + _position;
        int read = _baseStream.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Cannot seek before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
