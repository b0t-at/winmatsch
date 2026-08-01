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
        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller()));
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller()));
        Installer installer = Assert.Single(analysis.Installers);

        Assert.Equal("{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}", metadata.AppId);
        Assert.Equal("{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}_is1", metadata.ProductCode);
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
        Assert.Null(installer.ElevationRequirement);
        Assert.Null(installer.InstallerLocale);
        Assert.Equal(@"%LOCALAPPDATA%\Contoso", installer.InstallationMetadata!.DefaultInstallLocation);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "INNO001");
    }

    [Theory]
    [InlineData("x64compatible", Architecture.X64)]
    [InlineData("arm64", Architecture.Arm64)]
    [InlineData("(x64compatible and (not arm64))", Architecture.X64)]
    [InlineData("x86os", Architecture.X86)]
    [InlineData("x86compatible and x64os", Architecture.X64)]
    [InlineData("x86compatible and arm64", Architecture.Arm64)]
    [InlineData("x64compatible and arm64", Architecture.Arm64)]
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
    public void X86compatible_without_payload_proof_is_inconclusive()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));

        Assert.Null(metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Equal("INNO001", Assert.Single(analysis.Diagnostics).Code);
        Assert.True(Assert.Single(analysis.Diagnostics).RequiresManualAnalysis);
    }

    [Fact]
    public void Public_inspect_preserves_invalid_data_exception_for_future_versions()
    {
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            Version = new Version(6, 5, 0),
        });
        using var stream = new MemoryStream(installer);
        using var peFile = new PeFile(stream);

        Assert.Throws<InvalidDataException>(() => new InnoProbe().Inspect(peFile, stream));
    }

    [Theory]
    [InlineData("x64compatible or not x64compatible")]
    [InlineData("not x64compatible")]
    [InlineData("(x86compatible or arm64)")]
    [InlineData("x64compatible and (")]
    [InlineData("x64compatible or")]
    [InlineData("x64compatible-suffix")]
    [InlineData("not (x86compatible or arm64)")]
    [InlineData("win64")]
    public void Non_single_or_malformed_architecture_expression_is_inconclusive(string expression)
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = expression,
            ArchitecturesInstallIn64BitMode = "",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Null(metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
    }

    [Fact]
    public void Architecture_expression_limits_fail_conservatively()
    {
        (string Expression, InnoProbeOptions ProbeOptions)[] cases =
        [
            ("x64compatible", new InnoProbeOptions { MaximumArchitectureExpressionCharacters = 8 }),
            (
                "x64compatible or x86compatible",
                new InnoProbeOptions { MaximumArchitectureExpressionTokens = 2 }),
            ("((x64compatible))", new InnoProbeOptions { MaximumArchitectureExpressionNesting = 1 }),
        ];

        foreach ((string expression, InnoProbeOptions probeOptions) in cases)
        {
            var fixtureOptions = new InnoFixtures.Options
            {
                ArchitecturesAllowed = expression,
                ArchitecturesInstallIn64BitMode = "",
            };

            InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(
                Inspect(InnoFixtures.BuildInstaller(fixtureOptions), new InnoProbe(probeOptions)));

            Assert.Null(metadata.EffectiveArchitecture);
            Assert.False(metadata.ArchitectureIsConclusive);
        }
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
    public void Strict_x86os_header_is_not_promoted_by_x64_payload()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86os",
            ArchitecturesInstallIn64BitMode = "",
            PayloadMachines = [Machine.Amd64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal([Architecture.X64], metadata.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X86, metadata.EffectiveArchitecture);
        Assert.True(metadata.ArchitectureIsConclusive);
    }

    [Theory]
    [InlineData(@"{autopf}\Contoso Commander", "x64compatible")]
    [InlineData(@"{pf}\Contoso Commander", "x64compatible")]
    [InlineData(@"{autopf}\Contoso Commander", "win64")]
    public void Arm64_payload_uses_64_bit_program_files_mode(string defaultDirName, string modeExpression)
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = modeExpression,
            DefaultDirName = defaultDirName,
            PayloadMachines = [Machine.Arm64],
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal(Architecture.Arm64, installer.Architecture);
        Assert.Equal(@"%ProgramFiles%\Contoso Commander", installer.InstallationMetadata!.DefaultInstallLocation);
    }

    [Theory]
    [InlineData(Machine.Amd64, Architecture.X64)]
    [InlineData(Machine.Arm64, Architecture.Arm64)]
    public void Win64_does_not_enable_x86compatible_payload_override(
        Machine payloadMachine,
        Architecture payloadArchitecture)
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "win64",
            ArchitecturesInstallIn64BitMode = "win64",
            PayloadMachines = [payloadMachine],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal([payloadArchitecture], metadata.EmbeddedPayloadArchitectures);
        Assert.Null(metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
    }

    [Theory]
    [InlineData(Machine.Amd64, @"%ProgramFiles%\Contoso Commander")]
    [InlineData(Machine.Arm64, @"%ProgramFiles(x86)%\Contoso Commander")]
    public void Mixed_64_bit_mode_predicate_is_evaluated_for_payload_architecture(
        Machine payloadMachine,
        string expectedPath)
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "x64compatible and not (arm64)",
            PayloadMachines = [payloadMachine],
        };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal(expectedPath, installer.InstallationMetadata!.DefaultInstallLocation);
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
    public void Undefined_cp1252_bytes_are_replaced_and_diagnosed_instead_of_crashing()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            AppNameBytesOverride = [0x81, (byte)'A'],
            OldArchitecturesAllowed = 0x04,
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Contains('\uFFFD', metadata.AppName!);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO004");
    }

    [Fact]
    public void Dbcs_code_page_strings_decode_without_replacement_diagnostics()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            AppName = "コマンダー",
            AppVerName = "コマンダー 2.5",
            Publisher = "コントソ",
            OldArchitecturesAllowed = 0x04,
            Languages = [new InnoFixtures.Language("日本語", 1041, 932)],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal("コマンダー", metadata.AppName);
        Assert.Equal("コントソ", metadata.Publisher);
        Assert.DoesNotContain(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO004");
    }

    [Fact]
    public void Out_of_range_ansi_code_page_falls_back_without_overflow()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            AnsiEncodingCodePageOverride = 1252,
            OldArchitecturesAllowed = 0x04,
            Languages = [new InnoFixtures.Language("future", 1033, uint.MaxValue)],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal("Contoso Commander", metadata.AppName);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO005");
    }

    [Fact]
    public void Empty_architectures_allowed_consults_embedded_payload()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "",
            ArchitecturesInstallIn64BitMode = "",
            PayloadMachines = [Machine.Amd64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.True(metadata.ArchitectureIsConclusive);
    }

    [Fact]
    public void Empty_architectures_allowed_consults_64_bit_mode_as_a_hint()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "",
            ArchitecturesInstallIn64BitMode = "arm64",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(Architecture.Arm64, metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO011");
    }

    [Fact]
    public void Dominant_largest_payload_is_selected_conservatively_from_mixed_evidence()
    {
        byte[] smallHelper = PeFixtures.BuildExe(Machine.I386);
        byte[] largeApplication = SquirrelFixtures.BuildResourceSetup(
            new byte[1024 * 1024],
            "DATA",
            999,
            Machine.Amd64);
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            AdditionalPayloadBytes = AdvancedInstallerFixtures.Concat(smallHelper, largeApplication),
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO002");
    }

    [Fact]
    public void Bzip2_payload_evidence_is_explicitly_unavailable()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            HeaderCompression = 2,
            AdditionalPayloadBytes = [1],
        };

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "INNO003");
        Assert.True(analysis.Diagnostics.Single(diagnostic => diagnostic.Code == "INNO003").RequiresManualAnalysis);
    }

    [Fact]
    public void Future_setup_data_version_returns_manual_analysis_instead_of_throwing()
    {
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            Version = new Version(6, 5, 0),
        });

        using var stream = new MemoryStream(installer);
        InstallerAnalysis analysis = FileAnalyzer.Analyze(stream, "future-setup.exe");

        Assert.Equal(DetectedInstallerFormat.InnoSetup, analysis.Format);
        Assert.Equal(InstallerType.Inno, Assert.Single(analysis.Installers).InstallerType);
        Assert.Equal("INNO010", Assert.Single(analysis.Diagnostics).Code);
        Assert.True(Assert.Single(analysis.Diagnostics).RequiresManualAnalysis);
    }

    [Fact]
    public void Legacy_x64_flag_is_strict_and_emits_unsupported_os_architectures()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            OldArchitecturesAllowed = 0x04,
            OldArchitecturesInstallIn64BitMode = 0x04,
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal("x64os", metadata.ArchitecturesAllowed);
        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.Equal([Architecture.X86, Architecture.Arm, Architecture.Arm64], installer.UnsupportedOSArchitectures);
    }

    [Fact]
    public void Legacy_x86_flag_is_strict_and_emits_unsupported_os_architectures()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            OldArchitecturesAllowed = 0x02,
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal("x86os", metadata.ArchitecturesAllowed);
        Assert.Equal(Architecture.X86, metadata.EffectiveArchitecture);
        Assert.Equal([Architecture.X64, Architecture.Arm, Architecture.Arm64], installer.UnsupportedOSArchitectures);
    }

    [Fact]
    public void Legacy_ia64_flag_is_preserved_as_manual_analysis()
    {
        var options = new InnoFixtures.Options
        {
            Version = new Version(5, 6, 0),
            Unicode = false,
            OldArchitecturesAllowed = 0x08,
        };

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));

        Assert.Null(Assert.Single(analysis.Installers).Architecture);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "INNO006");
    }

    [Fact]
    public void Current_header_emits_supported_unsupported_os_architecture_evidence()
    {
        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller())).Installers);

        Assert.Equal([Architecture.X86, Architecture.Arm], installer.UnsupportedOSArchitectures);
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
    public void Corrupt_resource_style_loader_table_is_not_silently_treated_as_generic()
    {
        var options = new InnoFixtures.Options
        {
            WriteLegacyLoaderPointer = false,
            CorruptLoaderChecksum = true,
        };

        Assert.Throws<InvalidDataException>(() => Inspect(InnoFixtures.BuildInstaller(options)));
    }

    [Fact]
    public void Unsupported_resource_style_loader_revision_is_not_silently_treated_as_generic()
    {
        var options = new InnoFixtures.Options
        {
            WriteLegacyLoaderPointer = false,
            LoaderRevision = 2,
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Contains("revision 2", exception.Message, StringComparison.Ordinal);
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
        Assert.Null(limited.EffectiveArchitecture);
        Assert.Equal([Architecture.X64], complete.EmbeddedPayloadArchitectures);
        Assert.Equal(Architecture.X64, complete.EffectiveArchitecture);
    }

    [Fact]
    public void Payload_candidate_limit_is_diagnosed_and_prevents_conclusive_architecture()
    {
        byte[] payload = AdvancedInstallerFixtures.Concat(
            DependencyFixtures.BuildPe(Machine.Amd64),
            DependencyFixtures.BuildPe(Machine.Amd64),
            DependencyFixtures.BuildPe(Machine.Arm64));
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            AdditionalPayloadBytes = payload,
        });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumPayloadCandidates = 2 });

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(installer, probe));

        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO013");
    }

    [Fact]
    public void Candidate_guard_diagnoses_unprocessed_compressed_payload_markers()
    {
        byte[] compressedArm64 = InnoFixtures.BuildMarkerPayload(
            0,
            DependencyFixtures.BuildPe(Machine.Arm64));
        byte[] payload = AdvancedInstallerFixtures.Concat(
            DependencyFixtures.BuildPe(Machine.Amd64),
            DependencyFixtures.BuildPe(Machine.Amd64),
            compressedArm64);
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            HeaderCompression = 1,
            AdditionalPayloadBytes = payload,
        });
        var probe = new InnoProbe(new InnoProbeOptions { MaximumPayloadCandidates = 2 });

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(installer, probe));

        Assert.Equal(Architecture.X64, metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO013");
    }

    [Fact]
    public void Exact_raw_scan_budget_diagnoses_unprocessed_compressed_payload_markers()
    {
        byte[] markerPayload = InnoFixtures.BuildMarkerPayload(
            0,
            DependencyFixtures.BuildPe(Machine.Amd64));
        byte[] installer = InnoFixtures.BuildInstaller(new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            HeaderCompression = 1,
            AdditionalPayloadBytes = markerPayload,
        });
        var probe = new InnoProbe(new InnoProbeOptions
        {
            MaximumAggregatePayloadBytes = markerPayload.Length,
        });

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(installer, probe));

        Assert.False(metadata.ArchitectureIsConclusive);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO009");
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
        Assert.Null(metadata.EffectiveArchitecture);
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
        Assert.Null(metadata.EffectiveArchitecture);
    }

    [Fact]
    public void Mixed_helper_and_application_pes_do_not_override_x86compatible()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible",
            ArchitecturesInstallIn64BitMode = "",
            AdditionalPayloadBytes = AdvancedInstallerFixtures.Concat(
                DependencyFixtures.BuildPe(Machine.I386),
                DependencyFixtures.BuildPe(Machine.Amd64)),
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Equal([Architecture.X86, Architecture.X64], metadata.EmbeddedPayloadArchitectures);
        Assert.Null(metadata.EffectiveArchitecture);
        Assert.Contains(metadata.Diagnostics, diagnostic => diagnostic.Code == "INNO002");
    }

    [Fact]
    public void Malformed_x86compatible_expression_does_not_enable_payload_override()
    {
        var options = new InnoFixtures.Options
        {
            ArchitecturesAllowed = "x86compatible and (",
            ArchitecturesInstallIn64BitMode = "",
            PayloadMachines = [Machine.Amd64],
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));

        Assert.Null(metadata.EffectiveArchitecture);
        Assert.False(metadata.ArchitectureIsConclusive);
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
    public void Escaped_literal_app_id_is_unescaped_before_product_code_generation()
    {
        var options = new InnoFixtures.Options
        {
            AppId = "{{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}",
        };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Equal("{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}", metadata.AppId);
        Assert.Equal("{A1B2C3D4-E5F6-47A8-9012-3456789ABCDE}_is1", metadata.ProductCode);
        Assert.Equal(metadata.ProductCode, installer.ProductCode);
    }

    [Fact]
    public void Escaped_runtime_app_id_is_still_rejected()
    {
        var options = new InnoFixtures.Options { AppId = "{{code:GetId}" };

        InnoSetupMetadata metadata = Assert.IsType<InnoSetupMetadata>(Inspect(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Null(metadata.AppId);
        Assert.Null(metadata.ProductCode);
        Assert.Null(installer.ProductCode);
    }

    [Theory]
    [InlineData("{cm:Version}")]
    [InlineData("{username}")]
    [InlineData("{code:GetVersion}")]
    [InlineData("{param:Version|0}")]
    [InlineData("{reg:HKCU\\Software\\Contoso,Version|0}")]
    [InlineData("{ini:{app}\\settings.ini,Version,Current|0}")]
    [InlineData("release-{sysuserinfoname}")]
    public void Unresolved_runtime_constants_are_rejected_from_arp_values(string value)
    {
        var options = new InnoFixtures.Options { AppVersion = value };

        Installer installer = Assert.Single(
            Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options))).Installers);

        Assert.Null(Assert.Single(installer.AppsAndFeaturesEntries!).DisplayVersion);
    }

    [Fact]
    public void Runtime_constants_do_not_leak_and_static_name_fallback_remains_safe()
    {
        var options = new InnoFixtures.Options
        {
            UninstallDisplayName = "{cm:DisplayName}",
            AppVerName = "{username}",
            AppName = "Static Commander",
            AppVersion = "{code:GetVersion}",
            Publisher = "Vendor {param:Publisher}",
            AppId = "{reg:HKCU\\Software\\Contoso,AppId}",
        };

        InstallerAnalysis analysis = Assert.IsType<InstallerAnalysis>(Probe(InnoFixtures.BuildInstaller(options)));
        Installer installer = Assert.Single(analysis.Installers);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);

        Assert.Equal("Static Commander", arp.DisplayName);
        Assert.Equal("Static Commander", analysis.ProductName);
        Assert.Null(arp.DisplayVersion);
        Assert.Null(arp.Publisher);
        Assert.Null(arp.ProductCode);
        Assert.Null(installer.ProductCode);
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
