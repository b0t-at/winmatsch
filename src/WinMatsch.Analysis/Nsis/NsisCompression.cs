using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;

namespace WinMatsch.Analysis.Nsis;

/// <summary>
/// Reads the decompressed NSIS installer header from the data that follows the first header.
/// NSIS stores the archive either non-solid — every block, the header first, is preceded by a
/// uint32 size whose high bit marks the block as compressed — or solid, where everything
/// after the first header is one continuous compressed stream that begins with the installer
/// header. Neither mode nor the compressor is declared anywhere, so both are inferred from
/// the first bytes the same way the NSIS stub and 7-Zip do:
/// <list type="number">
/// <item>first uint32 equals the declared header size → non-solid, stored (uncompressed build);</item>
/// <item>an LZMA property header (0x5D props byte, dictionary size with zero low bytes, with
/// an optional leading filter-flag byte) at the data start → solid LZMA;</item>
/// <item>first uint32 has the high bit set → non-solid with a compressed header block whose
/// compressor is inferred from the block's first bytes by the same signatures;</item>
/// <item>the NSIS-modified bzip2 block magic (0x31, then a byte &lt; 14) at the data start →
/// solid bzip2;</item>
/// <item>anything else → solid deflate (NSIS zlib mode is a raw deflate stream, no wrapper).</item>
/// </list>
/// Supported compressors are raw deflate and raw LZMA1 (5-byte property header, no size
/// field). NSIS's bzip2 is a modified format without the standard stream header that ordinary
/// bzip2 decoders reject, and NSIS builds rarely use it — it is detected and reported as
/// unsupported. The LZMA filter flag 1 (BCJ x86 filter) is likewise reported as unsupported.
/// </summary>
internal static class NsisCompression
{
    private const uint CompressedFlag = 0x80000000;
    private const byte LzmaPropsByte = 0x5D;
    private const byte Bzip2BlockMagic = 0x31;

    /// <summary>Reads and decompresses the installer header declared by the first header.</summary>
    /// <exception cref="InvalidDataException">
    /// The archive is truncated, uses an unsupported compressor, or does not decompress.
    /// </exception>
    public static byte[] ReadHeaderData(Stream stream, NsisFirstHeader firstHeader)
    {
        long available = stream.Length - firstHeader.DataOffset;
        if (available < 4)
        {
            throw new InvalidDataException("The NSIS archive is truncated: no data follows the first header.");
        }

        Span<byte> prefix = stackalloc byte[16];
        int prefixLength = (int)Math.Min(prefix.Length, available);
        stream.Position = firstHeader.DataOffset;
        stream.ReadExactly(prefix[..prefixLength]);
        prefix = prefix[..prefixLength];

        uint firstDword = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (firstDword == (uint)firstHeader.HeaderSize)
        {
            return ReadStored(stream, firstHeader.DataOffset + 4, firstHeader.HeaderSize, available - 4);
        }

        if (IsLzma(prefix))
        {
            return Decompress(CreateLzmaStream(stream, firstHeader.DataOffset, available), firstHeader.HeaderSize);
        }

        // The high-bit test must precede the solid-bzip2 test: a non-solid size prefix like
        // 0x800002xx has a low byte that can look like the bzip2 block magic.
        if ((firstDword & CompressedFlag) != 0)
        {
            long blockSize = firstDword & ~CompressedFlag;
            if (blockSize < 1 || blockSize > available - 4)
            {
                throw new InvalidDataException(
                    "The NSIS archive is truncated: the compressed installer header block extends past the end of the file.");
            }

            ReadOnlySpan<byte> block = prefix.Length > 4 ? prefix[4..] : [];
            if (IsBzip2(block))
            {
                throw UnsupportedBzip2();
            }

            Stream data = IsLzma(block)
                ? CreateLzmaStream(stream, firstHeader.DataOffset + 4, blockSize)
                : CreateDeflateStream(stream, firstHeader.DataOffset + 4, blockSize);
            return Decompress(data, firstHeader.HeaderSize);
        }

        if (IsBzip2(prefix))
        {
            throw UnsupportedBzip2();
        }

        return Decompress(CreateDeflateStream(stream, firstHeader.DataOffset, available), firstHeader.HeaderSize);
    }

    /// <summary>
    /// An LZMA property header: the props byte 0x5D that every NSIS build emits (lc=3, lp=0,
    /// pb=2) followed by the dictionary size, always a multiple of 64 KiB, so its low two
    /// bytes are zero. Also matched with a leading filter-flag byte (0 or 1).
    /// </summary>
    private static bool IsLzma(ReadOnlySpan<byte> data)
        => IsLzmaProperties(data) || (data.Length >= 1 && data[0] <= 1 && IsLzmaProperties(data[1..]));

    private static bool IsLzmaProperties(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == LzmaPropsByte && data[1] == 0 && data[2] == 0;

    /// <summary>The NSIS-modified bzip2 block magic: no "BZh" stream header, level byte below 14.</summary>
    private static bool IsBzip2(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == Bzip2BlockMagic && data[1] < 14;

    private static InvalidDataException UnsupportedBzip2()
        => new(
            "The NSIS installer uses NSIS-modified bzip2 compression, which this analyzer cannot decode safely. "
            + "Manual analysis is required; do not infer the installer type or architecture from the x86 stub.");

    private static byte[] ReadStored(Stream stream, long offset, int headerSize, long available)
    {
        if (headerSize > available)
        {
            throw new InvalidDataException(
                "The NSIS archive is truncated: the stored installer header extends past the end of the file.");
        }

        byte[] header = new byte[headerSize];
        stream.Position = offset;
        stream.ReadExactly(header);
        return header;
    }

    /// <summary>
    /// A raw-LZMA1 decoder over the NSIS 5-byte property header (props byte + dictionary
    /// size; no uncompressed-size field). A leading filter-flag byte of 0 is skipped; 1 means
    /// the data went through NSIS's BCJ x86 filter, which is not supported.
    /// </summary>
    private static LzmaStream CreateLzmaStream(Stream stream, long offset, long length)
    {
        var data = new BoundedReadStream(stream, offset, length);
        Span<byte> first = stackalloc byte[1];
        data.ReadExactly(first);
        if (first[0] <= 1)
        {
            if (first[0] == 1)
            {
                throw new InvalidDataException(
                    "The NSIS installer uses LZMA with the BCJ x86 filter, which this analyzer cannot decode safely. "
                    + "Manual analysis is required; do not infer the installer type or architecture from the x86 stub.");
            }

            data.ReadExactly(first); // Filter flag 0: the props byte follows.
        }

        byte[] properties = new byte[5];
        properties[0] = first[0];
        data.ReadExactly(properties.AsSpan(1));
        uint dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
        if (dictionarySize == 0 || dictionarySize > AnalysisLimits.MaxNsisHeaderBytes)
        {
            data.Dispose();
            throw new InvalidDataException(
                $"The NSIS LZMA dictionary declares {dictionarySize} bytes; "
                + $"the analysis limit is {AnalysisLimits.MaxNsisHeaderBytes} bytes.");
        }

        return new LzmaStream(properties, data);
    }

    private static DeflateStream CreateDeflateStream(Stream stream, long offset, long length)
        => new(new BoundedReadStream(stream, offset, length), CompressionMode.Decompress);

    private static byte[] Decompress(Stream data, int headerSize)
    {
        byte[] header = new byte[headerSize];
        try
        {
            data.ReadExactly(header);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                "The NSIS installer header ends before the size declared by the first header.", exception);
        }
        catch (SharpCompressException exception)
        {
            throw new InvalidDataException("The NSIS installer header is not valid LZMA data.", exception);
        }
        finally
        {
            data.Dispose();
        }

        return header;
    }

    /// <summary>
    /// A read-only, forward-only view of a slice of the underlying stream. Disposing the view
    /// leaves the underlying stream open — probes must not dispose the analysis stream.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _stream;
        private long _remaining;

        public BoundedReadStream(Stream stream, long offset, long length)
        {
            _stream = stream;
            _stream.Position = offset;
            _remaining = Math.Min(length, stream.Length - offset);
        }

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
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int read = _stream.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
