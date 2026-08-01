using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace WinMatsch.Analysis.Tests;

internal static class DependencyFixtures
{
    public static byte[] BuildPe(Machine machine, params string[] imports)
    {
        var builder = new ImportPeBuilder(machine, imports);
        var output = new BlobBuilder();
        builder.Serialize(output);
        return output.ToArray();
    }
    public static MemoryStream BuildZip(params (string Path, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static SparsePrefixStream BuildSparsePeStream(
        Machine machine,
        long length,
        params string[] imports)
        => new(BuildPe(machine, imports), length);

    public static Stream AsNonSeekable(MemoryStream stream) => new NonSeekableReadStream(stream);

    public static byte[] AddCentralDirectoryDigitalSignature(
        byte[] archive,
        ReadOnlySpan<byte> signatureData)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(signatureData.Length, ushort.MaxValue);
        int eocdOffset = archive.Length - 22;
        byte[] record = new byte[6 + signatureData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x05054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), checked((ushort)signatureData.Length));
        signatureData.CopyTo(record.AsSpan(6));
        byte[] result = new byte[archive.Length + record.Length];
        archive.AsSpan(0, eocdOffset).CopyTo(result);
        record.CopyTo(result, eocdOffset);
        archive.AsSpan(eocdOffset).CopyTo(result.AsSpan(eocdOffset + record.Length));
        int newEocdOffset = eocdOffset + record.Length;
        uint oldDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(newEocdOffset + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(newEocdOffset + 12),
            checked(oldDirectorySize + (uint)record.Length));
        return result;
    }

    public static byte[] AddArchiveExtraDataRecord(
        byte[] archive,
        ReadOnlySpan<byte> extraData)
    {
        ArgumentNullException.ThrowIfNull(archive);
        int eocdOffset = archive.Length - 22;
        int directoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            archive.AsSpan(eocdOffset + 16)));
        byte[] record = new byte[8 + extraData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x08064B50);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), checked((uint)extraData.Length));
        extraData.CopyTo(record.AsSpan(8));
        byte[] result = new byte[archive.Length + record.Length];
        archive.AsSpan(0, directoryOffset).CopyTo(result);
        record.CopyTo(result, directoryOffset);
        archive.AsSpan(directoryOffset).CopyTo(result.AsSpan(directoryOffset + record.Length));
        int newEocdOffset = eocdOffset + record.Length;
        uint oldDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(newEocdOffset + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(newEocdOffset + 16),
            checked(oldDirectoryOffset + (uint)record.Length));
        return result;
    }

    public static MemoryStream BuildCompressedZeroZip(string path, long uncompressedLength)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
            using Stream destination = entry.Open();
            byte[] zeros = new byte[64 * 1024];
            long remaining = uncompressedLength;
            while (remaining > 0)
            {
                int count = (int)Math.Min(zeros.Length, remaining);
                destination.Write(zeros, 0, count);
                remaining -= count;
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static MemoryStream BuildDeflateWorkAmplificationZip(
        string path,
        int emptyBlockCount,
        byte payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(emptyBlockCount);
        using var compressed = new MemoryStream();
        for (int i = 0; i < emptyBlockCount; i++)
        {
            compressed.Write([0x00, 0x00, 0x00, 0xFF, 0xFF]);
        }
        compressed.Write([0x01, 0x01, 0x00, 0xFE, 0xFF, payload]);

        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] content = [payload];
        uint crc = SevenZipFixtures.Crc32(content);
        var stream = new MemoryStream();
        WriteUInt32(stream, 0x04034B50);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 8);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, crc);
        WriteUInt32(stream, checked((uint)compressed.Length));
        WriteUInt32(stream, 1);
        WriteUInt16(stream, checked((ushort)pathBytes.Length));
        WriteUInt16(stream, 0);
        stream.Write(pathBytes);
        compressed.Position = 0;
        compressed.CopyTo(stream);

        long centralOffset = stream.Position;
        WriteUInt32(stream, 0x02014B50);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, 20);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 8);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, crc);
        WriteUInt32(stream, checked((uint)compressed.Length));
        WriteUInt32(stream, 1);
        WriteUInt16(stream, checked((ushort)pathBytes.Length));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        stream.Write(pathBytes);
        long centralSize = stream.Position - centralOffset;

        WriteUInt32(stream, 0x06054B50);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, checked((uint)centralSize));
        WriteUInt32(stream, checked((uint)centralOffset));
        WriteUInt16(stream, 0);
        stream.Position = 0;
        return stream;
    }

    public static byte[] BuildStructurallyInvalidPeHeader()
    {
        byte[] image = new byte[512];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 64);
        "PE\0\0"u8.CopyTo(image.AsSpan(64));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(68), (ushort)Machine.Amd64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(70), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(84), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(86),
            (ushort)Characteristics.ExecutableImage);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(88), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(120), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(124), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(144), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(148), 512);
        return image;
    }

    public static byte[] RuntimeConfig(string version) => Encoding.UTF8.GetBytes(
        $$"""
          {
            "runtimeOptions": {
              "framework": {
                "name": "Microsoft.NETCore.App",
                "version": "{{version}}"
              }
            }
          }
          """);

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class ImportPeBuilder : PEBuilder
    {
        private readonly PEDirectoriesBuilder _directories = new();
        private readonly string[] _imports;

        public ImportPeBuilder(Machine machine, string[] imports)
            : base(
                new PEHeaderBuilder(
                    machine: machine,
                    imageCharacteristics: Characteristics.ExecutableImage),
                deterministicIdProvider: static _ => new BlobContentId(
                    new Guid("A92CD521-23DC-4887-9589-3454FC21D98A"),
                    0x5EED1234))
        {
            _imports = imports;
        }

        protected override ImmutableArray<Section> CreateSections()
            => [new Section(".idata", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead)];

        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            var builder = new BlobBuilder();
            int descriptorsSize = (_imports.Length + 1) * 20;
            int nameOffset = descriptorsSize;

            foreach (string import in _imports)
            {
                builder.WriteUInt32(0); // OriginalFirstThunk
                builder.WriteUInt32(0); // TimeDateStamp
                builder.WriteUInt32(0); // ForwarderChain
                builder.WriteUInt32((uint)(location.RelativeVirtualAddress + nameOffset));
                builder.WriteUInt32(0); // FirstThunk
                nameOffset += Encoding.ASCII.GetByteCount(import) + 1;
            }

            builder.WriteBytes(new byte[20]); // Null IMAGE_IMPORT_DESCRIPTOR terminator.
            foreach (string import in _imports)
            {
                builder.WriteUTF8(import, allowUnpairedSurrogates: false);
                builder.WriteByte(0);
            }

            _directories.ImportTable = new DirectoryEntry(location.RelativeVirtualAddress, descriptorsSize);
            return builder;
        }

        protected override PEDirectoriesBuilder GetDirectories() => _directories;
    }

    internal sealed class SparsePrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly long _length;
        private long _position;

        public SparsePrefixStream(byte[] prefix, long length)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            ArgumentOutOfRangeException.ThrowIfLessThan(length, prefix.Length);

            _prefix = prefix;
            _length = length;
        }

        public long TotalBytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, _length - _position);
            if (count <= 0)
            {
                return 0;
            }

            buffer[..count].Clear();
            if (_position < _prefix.Length)
            {
                int prefixBytes = (int)Math.Min(count, _prefix.Length - _position);
                _prefix.AsSpan((int)_position, prefixBytes).CopyTo(buffer);
            }

            _position += count;
            TotalBytesRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (position < 0)
            {
                throw new IOException("Cannot seek before the stream.");
            }

            _position = position;
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableReadStream(MemoryStream inner) : Stream
    {
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
            => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
