using System.Reflection.PortableExecutable;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class PeFileTests
{
    [Theory]
    [InlineData(Machine.Amd64, Architecture.X64)]
    [InlineData(Machine.I386, Architecture.X86)]
    [InlineData(Machine.Arm64, Architecture.Arm64)]
    [InlineData(Machine.Arm, Architecture.Arm)]
    [InlineData(Machine.Thumb, Architecture.Arm)]
    [InlineData(Machine.ArmThumb2, Architecture.Arm)]
    public void Architecture_is_mapped_from_the_coff_machine(Machine machine, Architecture expected)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(machine: machine);
        using var peFile = new PeFile(stream);

        Assert.Equal(expected, peFile.Architecture);
    }

    [Fact]
    public void Unknown_machine_falls_back_to_x86()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(machine: Machine.IA64);
        using var peFile = new PeFile(stream);

        Assert.Equal(Architecture.X86, peFile.Architecture);
    }

    [Fact]
    public void IsDll_reflects_the_image_characteristics()
    {
        using var exeStream = new MemoryStream(PeFixtures.BuildExe(isDll: false));
        using var dllStream = new MemoryStream(PeFixtures.BuildExe(isDll: true));

        using var exe = new PeFile(exeStream);
        using var dll = new PeFile(dllStream);

        Assert.False(exe.IsDll);
        Assert.True(dll.IsDll);
    }

    [Fact]
    public void Version_strings_are_parsed_from_the_version_resource()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(
            ProductName: "Contoso App",
            CompanyName: "Contoso Ltd.",
            LegalCopyright: "© 2026 Contoso Ltd.",
            ProductVersion: "2.4.6",
            FileVersion: "2.4.6.0",
            OriginalFilename: "ContosoApp.exe",
            FileDescription: "Contoso Application"));
        using var peFile = new PeFile(stream);

        Assert.Equal("Contoso App", peFile.VersionInfo.ProductName);
        Assert.Equal("Contoso Ltd.", peFile.VersionInfo.CompanyName);
        Assert.Equal("© 2026 Contoso Ltd.", peFile.VersionInfo.LegalCopyright);
        Assert.Equal("2.4.6", peFile.VersionInfo.ProductVersion);
        Assert.Equal("2.4.6.0", peFile.VersionInfo.FileVersion);
        Assert.Equal("ContosoApp.exe", peFile.VersionInfo.OriginalFilename);
        Assert.Equal("Contoso Application", peFile.VersionInfo.FileDescription);
    }

    [Fact]
    public void Version_strings_are_null_without_a_version_resource()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();
        using var peFile = new PeFile(stream);

        Assert.Null(peFile.VersionInfo.ProductName);
        Assert.Null(peFile.VersionInfo.CompanyName);
        Assert.Null(peFile.VersionInfo.LegalCopyright);
        Assert.Null(peFile.VersionInfo.ProductVersion);
        Assert.Null(peFile.VersionInfo.FileVersion);
        Assert.Null(peFile.VersionInfo.OriginalFilename);
        Assert.Null(peFile.VersionInfo.FileDescription);
    }

    [Fact]
    public void Partial_version_resource_leaves_missing_strings_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Only Name"));
        using var peFile = new PeFile(stream);

        Assert.Equal("Only Name", peFile.VersionInfo.ProductName);
        Assert.Null(peFile.VersionInfo.CompanyName);
        Assert.Null(peFile.VersionInfo.FileDescription);
    }

    [Fact]
    public void RequireAdministrator_manifest_maps_to_elevation_required_and_machine_scope_hint()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(manifestXml: PeFixtures.ManifestXml("requireAdministrator"));
        using var peFile = new PeFile(stream);

        Assert.Equal(ElevationRequirement.ElevationRequired, peFile.RequestedElevation);
        Assert.Equal(Scope.Machine, peFile.ScopeHint);
    }

    [Theory]
    [InlineData("asInvoker")]
    [InlineData("highestAvailable")]
    public void Non_administrator_manifest_levels_map_to_null(string level)
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(manifestXml: PeFixtures.ManifestXml(level));
        using var peFile = new PeFile(stream);

        Assert.Null(peFile.RequestedElevation);
        Assert.Null(peFile.ScopeHint);
    }

    [Fact]
    public void Missing_manifest_maps_to_null_elevation()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();
        using var peFile = new PeFile(stream);

        Assert.Null(peFile.RequestedElevation);
    }

    [Fact]
    public void Malformed_manifest_xml_is_tolerated()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(manifestXml: "<assembly><unclosed");
        using var peFile = new PeFile(stream);

        Assert.Null(peFile.RequestedElevation);
    }

    [Fact]
    public void Oversized_resource_directory_is_rejected_before_materialization()
    {
        byte[] executable = PeFixtures.BuildExe(version: new VersionStrings(ProductName: "Ignored"));
        int peHeaderOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(executable.AsSpan(0x3C));
        int optionalHeaderOffset = peHeaderOffset + 24;
        ushort magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(executable.AsSpan(optionalHeaderOffset));
        int dataDirectoriesOffset = optionalHeaderOffset + (magic == 0x20B ? 112 : 96);
        int resourceDirectorySizeOffset = dataDirectoriesOffset + (2 * 8) + 4;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            executable.AsSpan(resourceDirectorySizeOffset),
            AnalysisLimits.MaxResourceBytes + 1);
        using var stream = new MemoryStream(executable);

        using var peFile = new PeFile(stream);

        Assert.Null(peFile.VersionInfo.ProductName);
    }

    [Fact]
    public void Non_pe_content_throws_bad_image_format()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<BadImageFormatException>(() => new PeFile(stream));
    }

    [Fact]
    public void The_stream_is_left_open()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream();
        using (var peFile = new PeFile(stream))
        {
            Assert.NotNull(peFile.VersionInfo);
        }

        Assert.True(stream.CanRead);
    }
}
