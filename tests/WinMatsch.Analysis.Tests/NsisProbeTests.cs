using WinMatsch.Analysis.Nsis;
using WinMatsch.Analysis.Pe;
using WinMatsch.Core;
using Xunit;

namespace WinMatsch.Analysis.Tests;

public class NsisProbeTests
{
    [Fact]
    public void Non_nsis_pe_returns_null()
    {
        using MemoryStream stream = PeFixtures.BuildExeStream(version: new VersionStrings(ProductName: "Tool"));
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        Assert.Null(new NsisProbe().Probe(peFile, stream));
    }

    [Fact]
    public void Overlay_without_the_signature_returns_null()
    {
        byte[] exe = PeFixtures.BuildExe();
        byte[] withOverlay = [.. exe, .. new byte[1024]];

        Assert.Null(Probe(withOverlay));
    }

    [Fact]
    public void Registry_writes_install_dir_and_langtable_are_harvested()
    {
        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller());

        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Nullsoft, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Nullsoft, installer.InstallerType);
        Assert.Equal(Architecture.X64, installer.Architecture); // $PROGRAMFILES64 promotes the x86 stub.
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
        Assert.Null(installer.ElevationRequirement);
        AppsAndFeaturesEntry arp = Assert.Single(installer.AppsAndFeaturesEntries!);
        Assert.Equal(NsisFixtures.DefaultDisplayName, arp.DisplayName);
        Assert.Equal(NsisFixtures.DefaultDisplayVersion, arp.DisplayVersion);
        Assert.Equal(NsisFixtures.DefaultPublisher, arp.Publisher);
        Assert.Equal(NsisFixtures.DefaultDisplayName, analysis.ProductName);
        Assert.Equal(NsisFixtures.DefaultPublisher, analysis.Publisher);
        Assert.Equal(NsisFixtures.DefaultDisplayVersion, analysis.ProductVersion);
    }

    [Theory]
    [InlineData(NsisCompressor.StoredNonSolid)]
    [InlineData(NsisCompressor.DeflateNonSolid)]
    [InlineData(NsisCompressor.DeflateSolid)]
    [InlineData(NsisCompressor.LzmaNonSolid)]
    [InlineData(NsisCompressor.LzmaSolid)]
    public void Every_supported_storage_mode_parses(NsisCompressor compressor)
    {
        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(new NsisFixtures.Options { Compressor = compressor }));

        Assert.NotNull(analysis);
        Assert.Equal(NsisFixtures.DefaultDisplayName, analysis.ProductName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unicode_and_ansi_strings_decode(bool unicode)
    {
        var options = new NsisFixtures.Options { Unicode = unicode };
        options.RegistryWrites[0] = options.RegistryWrites[0] with
        {
            Value = [NsisFixtures.Token.Lit("Café Olé")],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal("Café Olé", analysis.ProductName);
    }

    [Fact]
    public void Local_app_data_install_dir_means_user_scope_without_promotion()
    {
        var options = new NsisFixtures.Options
        {
            // $LOCALAPPDATA is CSIDL 0x1C for the user, CSIDL_COMMON_APPDATA for all users.
            InstallDirectory = [NsisFixtures.Token.Shell(0x1C, 0x23), NsisFixtures.Token.Lit(@"\Contoso")],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Scope.User, installer.Scope);
        Assert.Equal(Architecture.X86, installer.Architecture);
    }

    [Fact]
    public void Thirty_two_bit_program_files_means_machine_scope_and_x86()
    {
        var options = new NsisFixtures.Options
        {
            InstallDirectory = [NsisFixtures.Token.ShellProgramFiles(x64: false), NsisFixtures.Token.Lit(@"\Contoso")],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Scope.Machine, installer.Scope);
        Assert.Equal(Architecture.X86, installer.Architecture);
    }

    [Fact]
    public void Unresolvable_install_dir_claims_no_scope()
    {
        var options = new NsisFixtures.Options
        {
            // An unknown registry shell folder decodes through its literal default, C:\Other.
            InstallDirectory = [NsisFixtures.Token.ShellRegistryUnknown(), NsisFixtures.Token.Lit(@"\Contoso")],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Installers).Scope);
    }

    [Fact]
    public void Missing_install_dir_claims_no_scope()
    {
        var options = new NsisFixtures.Options { InstallDirectory = null };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Installers).Scope);
    }

    [Fact]
    public void Set_reg_view_64_promotes_the_architecture()
    {
        var options = new NsisFixtures.Options
        {
            InstallDirectory = [NsisFixtures.Token.Lit(@"C:\Contoso")],
            SetRegView64 = true,
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.X64, Assert.Single(analysis.Installers).Architecture);
    }

    [Theory]
    [InlineData("app-64.7z", Architecture.X64)]
    [InlineData("app-x64.7z", Architecture.X64)]
    [InlineData("app-arm64.7z", Architecture.Arm64)]
    [InlineData("app-32.7z", Architecture.X86)]
    [InlineData("app-ia32.7z", Architecture.X86)]
    public void Electron_payload_name_drives_architecture(string payloadName, Architecture expected)
    {
        var options = new NsisFixtures.Options
        {
            InstallDirectory = [NsisFixtures.Token.ShellProgramFiles(x64: false)],
            PayloadNames = [payloadName],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal(expected, Assert.Single(analysis.Installers).Architecture);
    }

    [Fact]
    public void Universal_electron_payloads_request_manual_analysis_without_claiming_neutral()
    {
        var options = new NsisFixtures.Options
        {
            InstallDirectory = [NsisFixtures.Token.ShellProgramFiles(x64: false)],
            PayloadNames = ["app-32.7z", "app-64.7z"],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal(Architecture.X86, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("NSIS001", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
    }

    [Fact]
    public void Lang_code_references_resolve_through_the_first_langtable()
    {
        var options = new NsisFixtures.Options { LangName = "Localized Name" };
        options.RegistryWrites[0] = options.RegistryWrites[0] with
        {
            Value = [NsisFixtures.Token.Lang(2)], // $(^Name): LANG_NAME is language string 2.
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal("Localized Name", analysis.ProductName);
    }

    [Fact]
    public void Variable_and_skip_codes_decode_symbolically()
    {
        var options = new NsisFixtures.Options();
        options.RegistryWrites[0] = options.RegistryWrites[0] with
        {
            Value =
            [
                NsisFixtures.Token.Var(10),
                NsisFixtures.Token.Skip('$'),
                NsisFixtures.Token.Lit(" edition"),
            ],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        // The unresolved $R0 is stripped from the ARP value; the escaped literal $ survives.
        Assert.Equal("$ edition", analysis.ProductName);
    }

    [Fact]
    public void Unresolved_user_variables_are_stripped_from_arp_values()
    {
        var options = new NsisFixtures.Options();
        options.RegistryWrites[0] = options.RegistryWrites[0] with
        {
            // Tauri writes DisplayName as "$(^Name) <version>$1"; $1 holds a runtime suffix.
            Value = [NsisFixtures.Token.Lit("Alma 0.0.927"), NsisFixtures.Token.Var(1)],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal("Alma 0.0.927", analysis.ProductName);
        AppsAndFeaturesEntry arp = Assert.Single(Assert.Single(analysis.Installers).AppsAndFeaturesEntries!);
        Assert.Equal("Alma 0.0.927", arp.DisplayName);
    }

    [Fact]
    public void An_arp_value_that_is_only_variables_falls_back_to_the_version_strings()
    {
        var options = new NsisFixtures.Options
        {
            Version = new VersionStrings(ProductName: "Stub Product"),
        };
        options.RegistryWrites[0] = options.RegistryWrites[0] with
        {
            Value = [NsisFixtures.Token.Var(1), NsisFixtures.Token.Var(12)],
        };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal("Stub Product", analysis.ProductName);
    }

    [Fact]
    public void Shctx_rooted_uninstall_writes_are_harvested()
    {
        var options = new NsisFixtures.Options();
        options.RegistryWrites =
        [
            new NsisFixtures.RegWrite(
                NsisFixtures.ShctxRoot,
                [NsisFixtures.Token.Lit(NsisFixtures.UninstallKey)],
                "DisplayName",
                [NsisFixtures.Token.Lit("Shctx App")]),
        ];

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal("Shctx App", analysis.ProductName);
    }

    [Fact]
    public void Non_uninstall_and_non_string_registry_writes_are_ignored()
    {
        var options = new NsisFixtures.Options
        {
            Version = new VersionStrings(ProductName: "Stub Product"),
        };
        options.RegistryWrites =
        [
            new NsisFixtures.RegWrite(
                NsisFixtures.HklmRoot,
                [NsisFixtures.Token.Lit(@"Software\Contoso")],
                "DisplayName",
                [NsisFixtures.Token.Lit("Not an ARP write")]),
            new NsisFixtures.RegWrite(
                NsisFixtures.HklmRoot,
                [NsisFixtures.Token.Lit(NsisFixtures.UninstallKey)],
                "DisplayName",
                [NsisFixtures.Token.Lit("4")],
                Type: 4), // REG_DWORD, as WriteRegDWORD compiles to.
        ];

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Installers).AppsAndFeaturesEntries);
        Assert.Equal("Stub Product", analysis.ProductName);
    }

    [Fact]
    public void No_registry_writes_falls_back_to_the_version_strings()
    {
        var options = new NsisFixtures.Options
        {
            Version = new VersionStrings(
                ProductName: "Stub Product",
                CompanyName: "Stub Co",
                ProductVersion: "9.9.9",
                LegalCopyright: "© Stub Co"),
        };
        options.RegistryWrites = [];

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Null(Assert.Single(analysis.Installers).AppsAndFeaturesEntries);
        Assert.Equal("Stub Product", analysis.ProductName);
        Assert.Equal("Stub Co", analysis.Publisher);
        Assert.Equal("9.9.9", analysis.ProductVersion);
        Assert.Equal("© Stub Co", analysis.Copyright);
    }

    [Fact]
    public void Elevation_requirement_comes_from_the_application_manifest()
    {
        var options = new NsisFixtures.Options { ManifestXml = PeFixtures.ManifestXml("requireAdministrator") };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal(ElevationRequirement.ElevationRequired, Assert.Single(analysis.Installers).ElevationRequirement);
    }

    [Theory]
    [InlineData((ushort)1031, "de-DE")]
    [InlineData((ushort)0, null)] // Language neutral: no locale claim.
    public void The_first_langtable_lcid_maps_to_the_locale(ushort lcid, string? expected)
    {
        var options = new NsisFixtures.Options { Lcid = lcid };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        LanguageTag? locale = Assert.Single(analysis.Installers).InstallerLocale;
        Assert.Equal(expected is null ? null : new LanguageTag(expected), locale);
    }

    [Fact]
    public void A_first_header_deeper_in_the_overlay_is_still_found()
    {
        var options = new NsisFixtures.Options { FirstHeaderPadding = 1024 };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Equal(NsisFixtures.DefaultDisplayName, analysis.ProductName);
    }

    [Fact]
    public void Langtables_before_strings_layout_parses()
    {
        // Tauri's NSIS builds emit the langtables block before the strings block.
        var options = new NsisFixtures.Options { LangtablesBeforeStrings = true };

        InstallerAnalysis? analysis = Probe(NsisFixtures.BuildInstaller(options));

        Assert.NotNull(analysis);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(NsisFixtures.DefaultDisplayName, analysis.ProductName);
        Assert.Equal(NsisFixtures.DefaultPublisher, analysis.Publisher);
        Assert.Equal(NsisFixtures.DefaultDisplayVersion, analysis.ProductVersion);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(Architecture.X64, installer.Architecture);
        Assert.Equal(new LanguageTag("en-US"), installer.InstallerLocale);
    }

    [Theory]
    [InlineData(NsisCompressor.Bzip2Solid)]
    [InlineData(NsisCompressor.Bzip2NonSolid)]
    public void Nsis_bzip2_degrades_naming_the_compressor(NsisCompressor compressor)
    {
        byte[] installer = NsisFixtures.BuildInstaller(new NsisFixtures.Options { Compressor = compressor });

        AnalysisDiagnostic diagnostic = AssertDegraded(Probe(installer));

        Assert.Contains("bzip2", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Manual analysis is required", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bcj_filtered_lzma_degrades_naming_the_filter()
    {
        byte[] installer = NsisFixtures.BuildInstaller(
            new NsisFixtures.Options { Compressor = NsisCompressor.LzmaBcjSolid });

        AnalysisDiagnostic diagnostic = AssertDegraded(Probe(installer));

        Assert.Contains("BCJ", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Manual analysis is required", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_lzma_dictionary_is_rejected_before_decoder_allocation()
    {
        byte[] installer = NsisFixtures.BuildInstaller(
            new NsisFixtures.Options { Compressor = NsisCompressor.OversizedLzmaDictionary });

        AnalysisDiagnostic diagnostic = AssertDegraded(Probe(installer));

        Assert.Contains("dictionary", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AnalysisLimits.MaxNsisHeaderBytes.ToString(), diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NsisCompressor.NoDataAtAll)]
    [InlineData(NsisCompressor.CorruptDeflateNonSolid)]
    [InlineData(NsisCompressor.CorruptLzmaSolid)]
    public void Corrupt_archives_degrade_with_a_diagnostic(NsisCompressor compressor)
    {
        byte[] installer = NsisFixtures.BuildInstaller(new NsisFixtures.Options { Compressor = compressor });

        AssertDegraded(Probe(installer));
    }

    [Fact]
    public void A_header_shorter_than_declared_degrades()
    {
        byte[] installer = NsisFixtures.BuildInstaller(
            new NsisFixtures.Options { DeclaredHeaderSizeOverride = 1 << 20 });

        AssertDegraded(Probe(installer));
    }

    [Fact]
    public void An_implausible_declared_header_size_degrades()
    {
        byte[] installer = NsisFixtures.BuildInstaller(
            new NsisFixtures.Options { DeclaredHeaderSizeOverride = -1 });

        AssertDegraded(Probe(installer));
    }

    [Fact]
    public void A_truncated_stored_header_degrades()
    {
        byte[] installer = NsisFixtures.BuildInstaller(
            new NsisFixtures.Options { Compressor = NsisCompressor.StoredNonSolid });

        AssertDegraded(Probe(installer[..^64]));
    }

    [Fact]
    public void The_stream_is_left_open()
    {
        using var stream = new MemoryStream(NsisFixtures.BuildInstaller());
        using var peFile = new PeFile(stream);
        stream.Position = 0;

        InstallerAnalysis? analysis = new NsisProbe().Probe(peFile, stream);

        Assert.NotNull(analysis);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
    }

    [Fact]
    public void ExeAnalyzer_detects_nsis_installers_end_to_end()
    {
        using var stream = new MemoryStream(NsisFixtures.BuildInstaller());

        InstallerAnalysis analysis = new ExeAnalyzer().Analyze(stream, "contoso-setup.exe");

        Assert.Equal(DetectedInstallerFormat.Nullsoft, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Nullsoft, installer.InstallerType);
        Assert.Equal(NsisFixtures.DefaultDisplayName, analysis.ProductName);
    }

    [Fact]
    public void ExeAnalyzer_uses_arm64_asset_name_to_override_generic_64_bit_nsis_script_evidence()
    {
        using var stream = new MemoryStream(NsisFixtures.BuildInstaller());

        InstallerAnalysis analysis = new ExeAnalyzer().Analyze(stream, "contoso-setup-arm64.exe");

        Assert.Equal(Architecture.Arm64, Assert.Single(analysis.Installers).Architecture);
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("NSIS004", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
    }

    private static InstallerAnalysis? Probe(byte[] installer)
    {
        using var stream = new MemoryStream(installer);
        using var peFile = new PeFile(stream);
        stream.Position = 0;
        return new NsisProbe().Probe(peFile, stream);
    }

    private static AnalysisDiagnostic AssertDegraded(InstallerAnalysis? analysis)
    {
        Assert.NotNull(analysis);
        Assert.Equal(DetectedInstallerFormat.Nullsoft, analysis.Format);
        Installer installer = Assert.Single(analysis.Installers);
        Assert.Equal(InstallerType.Nullsoft, installer.InstallerType);
        Assert.Null(installer.Architecture); // The x86 stub says nothing about the payload.
        AnalysisDiagnostic diagnostic = Assert.Single(analysis.Diagnostics);
        Assert.Equal("NSIS003", diagnostic.Code);
        Assert.True(diagnostic.RequiresManualAnalysis);
        return diagnostic;
    }
}
