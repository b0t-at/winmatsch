using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using WinMatsch.Analysis.Squirrel;
using DotNetZipArchive = System.IO.Compression.ZipArchive;
using DotNetZipArchiveEntry = System.IO.Compression.ZipArchiveEntry;
using ExtendedZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace WinMatsch.Analysis;

/// <summary>
/// Opens a pre-bounded ZIP and adds Deflate64 reads without broadening the accepted method set.
/// </summary>
internal sealed class SupportedZipArchive : IDisposable
{
    private const ushort EncryptedFlag = 1 << 0;
    private const ushort StrongEncryptionFlag = 1 << 6;
    private const ushort MaskedHeaderValuesFlag = 1 << 13;
    private const ushort Stored = 0;
    private const ushort Deflate = 8;
    private const ushort Deflate64 = 9;
    private const ushort WinZipAes = 99;

    private readonly DotNetZipArchive _archive;
    private readonly IArchive? _extendedArchive;

    public SupportedZipArchive(
        Stream stream,
        string archiveName,
        string description,
        int maximumEntryCount = AnalysisLimits.MaxArchiveEntries,
        long maximumCentralDirectoryBytes = AnalysisLimits.MaxDependencyCentralDirectoryBytes,
        bool validateAllEntryFeatures = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        IReadOnlyList<ZipCentralDirectoryEntry> directory = ZipArchiveBounds.Inspect(
            stream,
            description,
            maximumEntryCount,
            maximumCentralDirectoryBytes);

        try
        {
            _archive = new DotNetZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (_archive.Entries.Count != directory.Count)
            {
                throw new InvalidDataException(
                    $"{description} exposes a different entry count than its validated ZIP directory.");
            }

            for (int index = 0; index < directory.Count; index++)
            {
                ValidateEntry(
                    archiveName,
                    _archive.Entries[index].FullName,
                    directory[index],
                    validateAllEntryFeatures);
            }

            if (directory.Any(static entry => entry.CompressionMethod == Deflate64))
            {
                stream.Position = 0;
                int deflate64Index = Enumerable.Range(0, directory.Count).First(
                    index => directory[index].CompressionMethod == Deflate64);
                string entryPath = _archive.Entries[deflate64Index].FullName;
                try
                {
                    _extendedArchive = ExtendedZipArchive.OpenArchive(
                        stream,
                        new ReaderOptions { LeaveStreamOpen = true });
                }
                catch (Exception exception) when (IsDecoderFailure(exception))
                {
                    throw InvalidEntry(
                        archiveName,
                        entryPath,
                        Deflate64,
                        "the Deflate64 archive metadata is malformed.",
                        exception);
                }
            }

            IArchiveEntry[] extendedEntries = _extendedArchive?.Entries.ToArray() ?? [];
            if (_extendedArchive is not null && extendedEntries.Length != directory.Count)
            {
                throw new InvalidDataException(
                    $"{description} exposes inconsistent entry counts across bounded ZIP readers.");
            }

            var entries = new SupportedZipArchiveEntry[directory.Count];
            for (int index = 0; index < directory.Count; index++)
            {
                DotNetZipArchiveEntry entry = _archive.Entries[index];
                IArchiveEntry? extendedEntry = directory[index].CompressionMethod == Deflate64
                    ? extendedEntries[index]
                    : null;
                if (extendedEntry is not null)
                {
                    ValidateExtendedEntry(entry, extendedEntry, description);
                }

                entries[index] = new SupportedZipArchiveEntry(
                    archiveName,
                    entry,
                    extendedEntry,
                    directory[index],
                    !validateAllEntryFeatures);
            }

            Entries = entries;
        }
        catch
        {
            _extendedArchive?.Dispose();
            _archive?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SupportedZipArchiveEntry> Entries { get; }

    public SupportedZipArchiveEntry? GetEntry(string fullName)
        => Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, fullName, StringComparison.Ordinal));

    public void Dispose()
    {
        _extendedArchive?.Dispose();
        _archive.Dispose();
    }

    public static string CompressionMethodName(ushort method)
        => method switch
        {
            0 => "Stored",
            1 => "Shrunk",
            2 => "Reduced (factor 1)",
            3 => "Reduced (factor 2)",
            4 => "Reduced (factor 3)",
            5 => "Reduced (factor 4)",
            6 => "Imploded",
            7 => "Tokenized (reserved)",
            8 => "Deflate",
            9 => "Deflate64",
            10 => "PKWARE DCL Implode",
            12 => "BZip2",
            14 => "LZMA",
            16 => "IBM z/OS CMPSC",
            18 => "IBM TERSE",
            19 => "IBM LZ77",
            20 => "Zstandard (deprecated method id)",
            93 => "Zstandard",
            94 => "MP3",
            95 => "XZ",
            96 => "JPEG",
            97 => "WavPack",
            98 => "PPMd",
            99 => "WinZip AES encryption marker",
            _ => "Unknown",
        };

    internal static void ValidateSupportedFeature(
        string archiveName,
        string entryPath,
        ZipCentralDirectoryEntry entry)
    {
        ValidateSupportedHeader(
            archiveName,
            entryPath,
            entry.GeneralPurposeBitFlags,
            entry.CompressionMethod);
        ValidateSupportedHeader(
            archiveName,
            entryPath,
            entry.LocalGeneralPurposeBitFlags,
            entry.LocalCompressionMethod);

        if (entry.CompressionMethod != entry.LocalCompressionMethod)
        {
            throw InvalidEntry(
                archiveName,
                entryPath,
                entry.CompressionMethod,
                $"the local header declares compression method {entry.LocalCompressionMethod} "
                    + $"({CompressionMethodName(entry.LocalCompressionMethod)}) while the central directory "
                    + $"declares method {entry.CompressionMethod} ({CompressionMethodName(entry.CompressionMethod)}).");
        }

        if (entry.GeneralPurposeBitFlags != entry.LocalGeneralPurposeBitFlags)
        {
            throw InvalidEntry(
                archiveName,
                entryPath,
                entry.CompressionMethod,
                $"the local header flags 0x{entry.LocalGeneralPurposeBitFlags:X4} do not match "
                    + $"the central directory flags 0x{entry.GeneralPurposeBitFlags:X4}.");
        }
    }

    private static void ValidateEntry(
        string archiveName,
        string entryPath,
        ZipCentralDirectoryEntry entry,
        bool validateFeatures)
    {
        if (validateFeatures)
        {
            ValidateSupportedFeature(archiveName, entryPath, entry);
            return;
        }

        if (entry.CompressionMethod != entry.LocalCompressionMethod)
        {
            ValidateSupportedHeader(
                archiveName,
                entryPath,
                entry.GeneralPurposeBitFlags,
                entry.CompressionMethod);
            ValidateSupportedHeader(
                archiveName,
                entryPath,
                entry.LocalGeneralPurposeBitFlags,
                entry.LocalCompressionMethod);
            throw InvalidEntry(
                archiveName,
                entryPath,
                entry.CompressionMethod,
                $"the local header declares compression method {entry.LocalCompressionMethod} "
                    + $"({CompressionMethodName(entry.LocalCompressionMethod)}) while the central directory "
                    + $"declares method {entry.CompressionMethod} ({CompressionMethodName(entry.CompressionMethod)}).");
        }

        if (entry.GeneralPurposeBitFlags != entry.LocalGeneralPurposeBitFlags)
        {
            ValidateSupportedHeader(
                archiveName,
                entryPath,
                entry.GeneralPurposeBitFlags,
                entry.CompressionMethod);
            ValidateSupportedHeader(
                archiveName,
                entryPath,
                entry.LocalGeneralPurposeBitFlags,
                entry.LocalCompressionMethod);
            throw InvalidEntry(
                archiveName,
                entryPath,
                entry.CompressionMethod,
                $"the local header flags 0x{entry.LocalGeneralPurposeBitFlags:X4} do not match "
                    + $"the central directory flags 0x{entry.GeneralPurposeBitFlags:X4}.");
        }
    }

    private static void ValidateSupportedHeader(
        string archiveName,
        string entryPath,
        ushort flags,
        ushort method)
    {
        string methodName = CompressionMethodName(method);
        if (method == WinZipAes)
        {
            throw new UnsupportedZipFeatureException(
                archiveName,
                entryPath,
                method,
                methodName,
                "WinZip AES encryption");
        }

        if ((flags & StrongEncryptionFlag) != 0)
        {
            throw new UnsupportedZipFeatureException(
                archiveName,
                entryPath,
                method,
                methodName,
                "strong ZIP encryption");
        }

        if ((flags & EncryptedFlag) != 0)
        {
            throw new UnsupportedZipFeatureException(
                archiveName,
                entryPath,
                method,
                methodName,
                "traditional ZIP encryption");
        }

        if ((flags & MaskedHeaderValuesFlag) != 0)
        {
            throw new UnsupportedZipFeatureException(
                archiveName,
                entryPath,
                method,
                methodName,
                "masked ZIP header values");
        }

        if (method is not Stored and not Deflate and not Deflate64)
        {
            throw new UnsupportedZipFeatureException(archiveName, entryPath, method, methodName);
        }
    }

    private static InvalidZipEntryDataException InvalidEntry(
        string archiveName,
        string entryPath,
        ushort method,
        string detail,
        Exception? innerException = null)
        => new(
            archiveName,
            entryPath,
            method,
            CompressionMethodName(method),
            detail,
            innerException);

    internal static bool IsDecoderFailure(Exception exception)
        => exception is SharpCompressException
            or InvalidDataException
            or EndOfStreamException
            or NotSupportedException;

    private static void ValidateExtendedEntry(
        DotNetZipArchiveEntry entry,
        IArchiveEntry extendedEntry,
        string description)
    {
        string extendedPath = (extendedEntry.Key ?? "").Replace('\\', '/');
        string dotNetPath = entry.FullName.Replace('\\', '/');
        if (!string.Equals(extendedPath, dotNetPath, StringComparison.Ordinal)
            || extendedEntry.Size != entry.Length
            || extendedEntry.CompressedSize != entry.CompressedLength)
        {
            throw new InvalidDataException(
                $"{description} exposes inconsistent Deflate64 entry metadata across bounded ZIP readers.");
        }
    }
}

internal sealed class SupportedZipArchiveEntry(
    string archiveName,
    DotNetZipArchiveEntry entry,
    IArchiveEntry? extendedEntry,
    ZipCentralDirectoryEntry directoryEntry,
    bool validateFeaturesOnOpen)
{
    public string FullName => entry.FullName;

    public long Length => entry.Length;

    public long CompressedLength => entry.CompressedLength;

    public ushort CompressionMethod { get; } = directoryEntry.CompressionMethod;

    public Stream Open()
    {
        if (validateFeaturesOnOpen)
        {
            SupportedZipArchive.ValidateSupportedFeature(archiveName, FullName, directoryEntry);
        }

        if (extendedEntry is null)
        {
            return entry.Open();
        }

        try
        {
            return new ClassifiedDeflate64Stream(
                extendedEntry.OpenEntryStream(),
                archiveName,
                FullName);
        }
        catch (Exception exception) when (SupportedZipArchive.IsDecoderFailure(exception))
        {
            throw InvalidEntry(exception);
        }
    }

    private InvalidZipEntryDataException InvalidEntry(Exception exception)
        => new(
            archiveName,
            FullName,
            CompressionMethod,
            SupportedZipArchive.CompressionMethodName(CompressionMethod),
            "the Deflate64 entry data is malformed.",
            exception);

    private sealed class ClassifiedDeflate64Stream(
        Stream source,
        string archiveName,
        string entryPath) : Stream
    {
        private const ushort CompressionMethod = 9;

        public override bool CanRead => source.CanRead;

        public override bool CanSeek => source.CanSeek;

        public override bool CanWrite => false;

        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override void Flush() => source.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => Read(() => source.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return source.Read(buffer);
            }
            catch (Exception exception) when (SupportedZipArchive.IsDecoderFailure(exception))
            {
                throw InvalidEntry(exception);
            }
        }

        public override int ReadByte() => Read(source.ReadByte);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(() => source.ReadAsync(buffer, offset, count, cancellationToken));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (SupportedZipArchive.IsDecoderFailure(exception))
            {
                throw InvalidEntry(exception);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Read(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (SupportedZipArchive.IsDecoderFailure(exception))
            {
                throw InvalidEntry(exception);
            }
        }

        private async Task<int> ReadAsync(Func<Task<int>> read)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (Exception exception) when (SupportedZipArchive.IsDecoderFailure(exception))
            {
                throw InvalidEntry(exception);
            }
        }

        private InvalidZipEntryDataException InvalidEntry(Exception exception)
            => new(
                archiveName,
                entryPath,
                CompressionMethod,
                SupportedZipArchive.CompressionMethodName(CompressionMethod),
                "the Deflate64 entry data is malformed.",
                exception);
    }
}
