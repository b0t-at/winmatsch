using System.Reflection.PortableExecutable;
using WinMatsch.Analysis.Inno;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class InnoProbeTests
{
    [Fact]
    public void Non_inno_pe_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(Machine.I386);
        using var peFile = new PeFile(stream);

        Assert.Null(new InnoProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Current_header_extracts_metadata_architecture_scope_and_arp()
    {
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller()));
        Installer installer = Assert.Single(analysis.Installers);

        Assert.Equal(DetectedInstallerFormat.InnoSetup, analysis.Format);
        Assert.Equal(InstallerType.Inno, installer.InstallerType);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal(ElevationRequirement.ElevationRequired, installer.ElevationRequirement);
        Assert.Equal("{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}_is1", installer.ProductCode);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        Assert.Equal(@"%ProgramFiles%\Contoso Commander", installer.InstallationMetadata!.DefaultInstallLocation);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Commander 2.5", arp.DisplayName);
        Assert.Equal("2.5.0", arp.DisplayVersion);
        Assert.Equal("Contoso Ltd", arp.Publisher);
        Assert.Equal(installer.ProductCode, arp.ProductCode);
    }

    [Fact]
    public void Old_ansi_header_preserves_multilingual_code_pages_and_user_scope()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            PrivilegesRequired = InnoPrivilegeLevel.Lowest,
            DefaultDirName = @"{localappdata}\Contoso",
            AppName = "Командир",
            AppVerName = "Командир 2.5",
            Publisher = "Контосо",
            OldArchitecturesAllowed = 0x02,
            Languages =
            [
                new InnoFixtures.Language("русский", 1049, 1251),
                new InnoFixtures.Language("українська", 1058, 1251),
            ],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(analysis.Installers);

        Assert.Equal(new Version(5, 6, 0), metadata.SetupDataVersion);
        Assert.False(metadata.IsUnicode);
        Assert.Equal("Командир", metadata.AppName);
        Assert.Equal("Контосо", metadata.Publisher);
        Assert.Equal([1251u, 1251u], metadata.Languages.Select(language => language.CodePage));
        Assert.Equal([new LanguageTag("ru-RU"), new LanguageTag("uk-UA")], metadata.Languages.Select(language => language.Locale));
        Assert.Equal(Architecture.X86, installer.Architecture);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal(ElevationRequirement.ElevationProhibited, installer.ElevationRequirement);
        Assert.Null(installer.InstallerLocale);
        Assert.Equal(@"%LOCALAPPDATA%\Contoso", installer.InstallationMetadata!.DefaultInstallLocation);
    }

    [Theory]
    [InlineData("x86compatible", Architecture.X86)]
    [InlineData("x64compatible", Architecture.X64)]
    [InlineData("arm64", Architecture.Arm64)]
    public void Current_architecture_expressions_map_when_unambiguous(string expression, Architecture expected)
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = expression,
            ArchitecturesInstallIn64BitMode = expression == "x86compatible" ? "" : expression,
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(expected, metadata.EffectiveArchitecture);
        Assert.True(metadata.ArchitectureIsConclusive);
    }

    [Fact]
    public void Mixed_architecture_expression_is_left_uncertain_without_payload_evidence()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible or x64compatible",
            ArchitecturesInstallIn64BitMode = "x64compatible",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Null(metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
    }

    [Fact]
    public void Keeper_style_x86compatible_header_yields_x64_from_embedded_payload()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            PayloadMachines = [Machine.Amd64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal("x86compatible", metadata.ArchitecturesAllowed);
        Assert.Equal([Architecture.X64], metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.True(metadata.ArchitectureIsConclusive);
    }

    [Fact]
    public void Arm64_embedded_payload_overrides_compatibility_header()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            PayloadMachines = [Machine.Arm64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(Architecture.Arm64, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Current_lzma_compressed_header_parses()
    {
        var options = new InnoFixtures.Options { CompressHeader = true };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal("Contoso Commander", metadata.AppName);
        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Lzma_dictionary_is_bounded_before_decoder_construction()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options
            {
                CompressHeader = true,
                LzmaDictionarySizeOverride = 512 * 1024 * 1024,
            });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumLzmaDictionaryBytes = 8 * 1024 * 1024 });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Probe(installer, probe));

        Assert.Contains("dictionary size", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binary_compiled_code_is_skipped_without_text_decoding()
    {
        var options = new InnoFixtures.Options
        {
            CompiledCode = [0x00, 0xD8, 0xFF, 0x00, 0x81],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal("Contoso Commander", metadata.AppName);
    }

    [Fact]
    public void Compiled_code_skip_is_bounded()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options { CompiledCode = new byte[1024] });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumCompiledCodeBytes = 128 });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Probe(installer, probe));

        Assert.Contains("compiled code", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Current_resource_style_loader_table_is_found_without_legacy_pointer()
    {
        var options = new InnoFixtures.Options { WriteLegacyLoaderPointer = false };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(new Version(6, 4, 0, 1), metadata.SetupDataVersion);
    }

    [Fact]
    public void Privilege_override_preserves_scope_and_elevation_uncertainty()
    {
        var options = new InnoFixtures.Options
        {
            PrivilegesRequired = InnoPrivilegeLevel.Admin,
            PrivilegeOverrides = 0x03,
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.True(metadata.PrivilegesMayBeOverridden);
        Assert.Null(metadata.Scope);
        Assert.Null(metadata.ElevationRequirement);
    }

    [Fact]
    public void Payload_marker_attempts_are_capped_even_when_markers_are_invalid()
    {
        byte[] markerPayload = InnoFixtures.BuildMarkerPayload(4, PeFixtures.BuildExe(Machine.Amd64));
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options
            {
                ArchitecturesAllowed = "x86compatible",
                ArchitecturesInstallIn64BitMode = "",
                HeaderCompression = 1,
                AdditionalPayloadBytes = markerPayload,
            });

        InnoSetupMetadata limited = Assert.IsType<InnoSetupMetadata>(
            Inspect(installer, new InnoProbe(new InnoProbeOptions { MaximumPayloadMarkerAttempts = 2 })));
        InnoSetupMetadata complete = Assert.IsType<InnoSetupMetadata>(Inspect(installer));

        Assert.Empty(limited.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X86, limited.EffectiveArchitecture);
        Assert.Equal([Architecture.X64], complete.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X64, complete.EffectiveArchitecture);
    }

    [Fact]
    public void Aggregate_payload_scan_budget_caps_decompression_work()
    {
        byte[] markerPayload = InnoFixtures.BuildMarkerPayload(0, PeFixtures.BuildExe(Machine.Amd64));
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options
            {
                ArchitecturesAllowed = "x86compatible",
                ArchitecturesInstallIn64BitMode = "",
                HeaderCompression = 1,
                AdditionalPayloadBytes = markerPayload,
            });
        var probe = new InnoProbe(
            new InnoProbeOptions { MaximumAggregatePayloadBytes = markerPayload.Length + 128 });

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(installer, probe));

        Assert.Empty(metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X86, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Pseudo_pe_is_not_accepted_as_payload_evidence()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            AdditionalPayloadBytes = InnoFixtures.BuildPseudoPe(Machine.Amd64),
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Empty(metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X86, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Mixed_helper_and_application_pes_do_not_override_x86compatible()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            PayloadMachines = [Machine.I386, Machine.Amd64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal([Architecture.X86, Architecture.X64], metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X86, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Helper_payload_does_not_override_non_x86compatible_header()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x64compatible",
            ArchitecturesInstallIn64BitMode = "x64compatible",
            PayloadMachines = [Machine.I386],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal([Architecture.X86], metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Uninstall_display_name_has_precedence_for_arp()
    {
        var options = new InnoFixtures.Options { UninstallDisplayName = "Commander ARP Name" };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal("Commander ARP Name", Assert.Single(installer.AppsAndFeaturesEntries!).DisplayName);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Disabled_uninstall_registration_suppresses_arp_and_product_code(
        bool createRegistryKey,
        bool uninstallable)
    {
        var options = new InnoFixtures.Options
        {
            CreateUninstallRegKey = createRegistryKey ? "yes" : "no",
            Uninstallable = uninstallable ? "yes" : "no",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(analysis.Installers);

        Assert.False(metadata.CreatesUninstallRegistryKey);
        Assert.Equal(options.CreateUninstallRegKey, metadata.CreateUninstallRegKey);
        Assert.Equal(options.Uninstallable, metadata.Uninstallable);
        Assert.Null(metadata.ProductCode);
        Assert.Null(installer.ProductCode);
        Assert.Null(installer.AppsAndFeaturesEntries);
        Assert.Equal(options.AppVerName, analysis.ProductName);
    }

    [Fact]
    public void Machine_x86_autopf_resolves_to_32_bit_program_files()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal(@"%ProgramFiles(x86)%\Contoso Commander", installer.InstallationMetadata!.DefaultInstallLocation);
    }

    [Fact]
    public void User_autopf_resolves_to_local_programs()
    {
        var options = new InnoFixtures.Options
        {
            PrivilegesRequired = InnoPrivilegeLevel.Lowest,
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal(@"%LOCALAPPDATA%\Programs\Contoso Commander", installer.InstallationMetadata!.DefaultInstallLocation);
    }

    [Fact]
    public void Pf_resolves_only_with_conclusive_machine_64_bit_mode()
    {
        var options = new InnoFixtures.Options { DefaultDirName = @"{pf}\Contoso Commander" };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal(@"%ProgramFiles%\Contoso Commander", installer.InstallationMetadata!.DefaultInstallLocation);
    }

    [Fact]
    public void Autopf_is_omitted_when_scope_or_install_mode_is_uncertain()
    {
        var options = new InnoFixtures.Options
        {
            PrivilegesRequired = InnoPrivilegeLevel.None,
            ArchitecturesAllowed = "x86compatible or x64compatible",
            ArchitecturesInstallIn64BitMode = "x64compatible",
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Null(installer.InstallationMetadata);
    }

    [Fact]
    public void Unsafe_arp_values_are_not_emitted()
    {
        var options = new InnoFixtures.Options
        {
            AppVerName = "ms-resource:AppName",
            AppId = "{code:GetId}",
            AppVersion = "$RUNTIME",
            Publisher = "Bad\u0001Publisher",
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal("Contoso Commander 2.5", arp.DisplayName);
        Assert.Null(arp.DisplayVersion);
        Assert.Null(arp.Publisher);
        Assert.Null(arp.ProductCode);
    }

    [Fact]
    public void Truncated_positive_inno_file_throws_clear_error()
    {
        byte[] installer = InnoFixtures.BuildInstaller();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Probe(installer[..^64]));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_loader_checksum_throws()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options { CorruptLoaderChecksum = true });

        Assert.Throws<InvalidDataException>(() => Probe(installer));
    }

    [Fact]
    public void Corrupt_header_checksum_throws()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options { CorruptHeaderChecksum = true });

        Assert.Throws<InvalidDataException>(() => Probe(installer));
    }

    [Fact]
    public void Declared_header_allocation_is_bounded_before_allocating()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options { StoredHeaderSizeOverride = 1024 });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumStoredHeaderBytes = 128 });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Probe(installer, probe));

        Assert.Contains("configured limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Individual_string_allocation_is_bounded()
    {
        byte[] installer = InnoFixtures.BuildInstaller(
            new InnoFixtures.Options { FirstStringLengthOverride = 4096 });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumStringBytes = 128 });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Probe(installer, probe));

        Assert.Contains("allocation limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stream_is_left_open()
    {
        using var stream = new MemoryStream(InnoFixtures.BuildInstaller());
        using var peFile = new PeFile(stream);

        Assert.NotNull(new InnoProbe().Probe(peFile, stream));
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
    }

    private static InstallerAnalysis? Probe(byte[] installer, InnoProbe? probe = null)
    {
        using var stream = new MemoryStream(installer);
        using var peFile = new PeFile(stream);
        return (probe ?? new InnoProbe()).Probe(peFile, stream);
    }

    private static InnoSetupMetadata? Inspect(byte[] installer, InnoProbe? probe = null)
    {
        using var stream = new MemoryStream(installer);
        using var peFile = new PeFile(stream);
        return (probe ?? new InnoProbe()).Inspect(peFile, stream);
    }
}
