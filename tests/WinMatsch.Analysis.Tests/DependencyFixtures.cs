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
}
