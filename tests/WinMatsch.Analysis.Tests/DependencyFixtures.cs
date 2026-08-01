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

    private sealed class ImportPeBuilder : PEBuilder
    {
        private readonly PEDirectoriesBuilder _directories = new();
        private readonly string[] _imports;

        public ImportPeBuilder(Machine machine, string[] imports)
            : base(
                new PEHeaderBuilder(
                    machine: machine,
                    imageCharacteristics: Characteristics.ExecutableImage),
                deterministicIdProvider: null)
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
}
