namespace WinMatsch.Analysis.Advanced;

/// <summary>
/// Locates overlay data appended after the last section of a PE image and
/// scans it for embedded payload signatures.
/// </summary>
/// <remarks>
/// Self-extracting installers (Advanced Installer's 7-Zip SFX, Squirrel's
/// bootstrap executable) append their payload archive after the PE image.
/// The overlay starts at the highest <c>PointerToRawData + SizeOfRawData</c>
/// across all section headers.
/// </remarks>
internal static class PeOverlay
{
    /// <summary>
    /// Computes the file offset where overlay data begins, or <c>0</c> when the
    /// PE structure cannot be parsed or the file has no overlay.
    /// </summary>
    /// <param name="stream">Seekable stream over the executable.</param>
    public static long GetStart(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> dosHeader = stackalloc byte[64];
        if (!TryReadAt(stream, 0, dosHeader) || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return 0;
        }

        uint peHeaderOffset = BitConverter.ToUInt32(dosHeader[60..]);

        Span<byte> coffHeader = stackalloc byte[24];
        if (!TryReadAt(stream, peHeaderOffset, coffHeader) || BitConverter.ToUInt32(coffHeader) != 0x00004550)
        {
            return 0;
        }

        ushort sectionCount = BitConverter.ToUInt16(coffHeader[6..]);
        ushort optionalHeaderSize = BitConverter.ToUInt16(coffHeader[20..]);
        long sectionTableOffset = peHeaderOffset + 24 + optionalHeaderSize;

        long overlayStart = 0;
        Span<byte> section = stackalloc byte[40];
        for (int i = 0; i < sectionCount; i++)
        {
            if (!TryReadAt(stream, sectionTableOffset + (i * 40L), section))
            {
                return 0;
            }

            uint sizeOfRawData = BitConverter.ToUInt32(section[16..]);
            uint pointerToRawData = BitConverter.ToUInt32(section[20..]);
            long sectionEnd = (long)pointerToRawData + sizeOfRawData;
            if (sectionEnd > overlayStart)
            {
                overlayStart = sectionEnd;
            }
        }

        return overlayStart >= stream.Length ? 0 : overlayStart;
    }

    /// <summary>
    /// Scans a bounded window of the stream for a payload signature.
    /// </summary>
    /// <param name="stream">Seekable stream over the executable.</param>
    /// <param name="searchStart">Absolute offset where the scan begins.</param>
    /// <param name="signature">Signature bytes to locate.</param>
    /// <param name="maxScanBytes">Maximum number of bytes to inspect.</param>
    /// <returns>The absolute offset of the signature, or <c>-1</c> when absent.</returns>
    public static long FindSignature(Stream stream, long searchStart, ReadOnlySpan<byte> signature, int maxScanBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long available = stream.Length - searchStart;
        if (available < signature.Length)
        {
            return -1;
        }

        int windowLength = (int)Math.Min(available, maxScanBytes);
        byte[] window = new byte[windowLength];
        if (!TryReadAt(stream, searchStart, window))
        {
            return -1;
        }

        int index = window.AsSpan().IndexOf(signature);
        return index < 0 ? -1 : searchStart + index;
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
