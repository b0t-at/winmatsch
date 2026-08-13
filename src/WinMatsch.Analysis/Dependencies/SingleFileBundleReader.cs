using System.Buffers.Binary;

namespace WinMatsch.Analysis.Dependencies;

/// <summary>
/// Reads the embedded <c>*.runtimeconfig.json</c> of a .NET single-file bundle. A bundled app
/// ships as one native apphost with its managed files appended, so the runtime configuration that
/// normally sits next to the executable is not visible on disk; without this reader the payload
/// looks like an unmanaged executable with no .NET dependency at all.
/// </summary>
/// <remarks>
/// Clean-room implementation of the publicly documented bundle layout: the apphost carries a
/// 32-byte bundle signature preceded by the 64-bit offset of the bundle header, and the header
/// (version 2 and later) records the offset and size of the runtime configuration.
/// </remarks>
internal static class SingleFileBundleReader
{
    // SHA-256 of ".net core bundle", embedded verbatim in every single-file apphost.
    private static readonly byte[] _bundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    private const int ScanChunkSize = 64 * 1024;
    private const int MaximumBundleIdBytes = 512;

    // The signature lives in the apphost stub that precedes the appended bundle payload, so it
    // is always near the start of the file; scanning further would read whole large payloads.
    private const long MaximumSignatureScanBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Returns the bundle's runtime-configuration bytes, or null when the stream is not a
    /// single-file bundle, the header is malformed, or the configuration exceeds
    /// <paramref name="maximumRuntimeConfigBytes"/>.
    /// </summary>
    public static byte[]? TryReadRuntimeConfig(Stream stream, int maximumRuntimeConfigBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || maximumRuntimeConfigBytes <= 0)
        {
            return null;
        }

        long savedPosition = stream.Position;
        try
        {
            long signatureOffset = FindSignature(stream);
            if (signatureOffset < sizeof(long))
            {
                return null;
            }

            stream.Position = signatureOffset - sizeof(long);
            if (!TryReadInt64(stream, out long headerOffset)
                || headerOffset <= 0
                || headerOffset >= stream.Length)
            {
                return null;
            }

            stream.Position = headerOffset;
            return TryReadRuntimeConfigFromHeader(stream, maximumRuntimeConfigBytes);
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private static byte[]? TryReadRuntimeConfigFromHeader(Stream stream, int maximumRuntimeConfigBytes)
    {
        if (!TryReadUInt32(stream, out uint majorVersion)
            || !TryReadUInt32(stream, out _)
            || !TryReadInt32(stream, out int fileCount)
            || fileCount < 0
            || majorVersion is 0 or > 16)
        {
            return null;
        }

        // The bundle id is a length-prefixed UTF-8 string; only its length matters here.
        if (!TryReadCompressedLength(stream, out int bundleIdLength)
            || bundleIdLength is < 0 or > MaximumBundleIdBytes
            || !TrySkip(stream, bundleIdLength))
        {
            return null;
        }

        // The runtime-configuration location was added in header version 2.
        if (majorVersion < 2
            || !TrySkip(stream, sizeof(long) * 2)
            || !TryReadInt64(stream, out long runtimeConfigOffset)
            || !TryReadInt64(stream, out long runtimeConfigSize))
        {
            return null;
        }

        if (runtimeConfigOffset <= 0
            || runtimeConfigSize <= 0
            || runtimeConfigSize > maximumRuntimeConfigBytes
            || runtimeConfigOffset + runtimeConfigSize > stream.Length)
        {
            return null;
        }

        stream.Position = runtimeConfigOffset;
        var content = new byte[runtimeConfigSize];
        return stream.ReadAtLeast(content, content.Length, throwOnEndOfStream: false) == content.Length
            ? content
            : null;
    }

    private static long FindSignature(Stream stream)
    {
        int signatureLength = _bundleSignature.Length;
        var buffer = new byte[ScanChunkSize + signatureLength];
        long chunkStart = 0;
        int carried = 0;
        stream.Position = 0;
        while (chunkStart < MaximumSignatureScanBytes)
        {
            int read = stream.Read(buffer, carried, buffer.Length - carried);
            if (read <= 0)
            {
                return -1;
            }

            int available = carried + read;
            int index = buffer.AsSpan(0, available).IndexOf(_bundleSignature);
            if (index >= 0)
            {
                return chunkStart + index;
            }

            carried = Math.Min(available, signatureLength - 1);
            buffer.AsSpan(available - carried, carried).CopyTo(buffer);
            chunkStart += available - carried;
        }

        return -1;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
        => stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;

    private static bool TryReadInt64(Stream stream, out long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        value = 0;
        if (!TryReadExactly(stream, buffer))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadInt32(Stream stream, out int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        value = 0;
        if (!TryReadExactly(stream, buffer))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(Stream stream, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        value = 0;
        if (!TryReadExactly(stream, buffer))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    /// <summary>Reads the 7-bit encoded length written by <c>BinaryWriter.Write(string)</c>.</summary>
    private static bool TryReadCompressedLength(Stream stream, out int value)
    {
        value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            int read = stream.ReadByte();
            if (read < 0)
            {
                return false;
            }

            value |= (read & 0x7F) << shift;
            if ((read & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkip(Stream stream, long count)
    {
        long target = stream.Position + count;
        if (target > stream.Length)
        {
            return false;
        }

        stream.Position = target;
        return true;
    }
}
