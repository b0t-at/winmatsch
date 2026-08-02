using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Inno;

internal sealed record InnoLoaderOffsets(long HeaderOffset, long DataOffset, bool PayloadEncrypted = false);

internal enum InnoCompression
{
    Stored,
    Zlib,
    Bzip2,
    Lzma1,
    Lzma2,
}

internal sealed record InnoParsedHeader(
    Version Version,
    bool Unicode,
    string? AppName,
    string? AppVerName,
    string? AppId,
    string? AppVersion,
    string? Publisher,
    string? DefaultDirName,
    string? UninstallDisplayName,
    string? CreateUninstallRegKey,
    string? Uninstallable,
    string? ArchitecturesAllowed,
    string? ArchitecturesInstallIn64BitMode,
    InnoPrivilegeLevel Privileges,
    bool PrivilegesMayBeOverridden,
    InnoCompression Compression,
    IReadOnlyList<InnoLanguage> Languages,
    bool HasUnsupportedIa64,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);

internal sealed record InnoPayloadCandidate(
    Architecture Architecture,
    long Size,
    PeImportInspection ImportInspection);

internal sealed record InnoPayloadScanResult(
    IReadOnlyList<InnoPayloadCandidate> Candidates,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    bool IsComplete);

internal sealed class UnsupportedInnoVersionException : Exception
{
    public UnsupportedInnoVersionException(Version version, bool isFuture)
        : base(
            isFuture
                ? $"Inno Setup setup-data version {version} is newer than the supported 7.0.0.3 family."
                : $"Inno Setup setup-data version {version} predates the supported 5.5.7 family.")
    {
        Version = version;
        IsFuture = isFuture;
    }

    public Version Version { get; }

    public bool IsFuture { get; }
}

internal sealed class UnsupportedInnoEncryptionException : Exception
{
    public UnsupportedInnoEncryptionException()
        : base("The Inno Setup primary header is fully encrypted and cannot be interpreted without its password.")
    {
    }
}

internal static partial class InnoFormatReader
{
    private static ReadOnlySpan<byte> ModernLoaderMagic
        => [0x72, 0x44, 0x6C, 0x50, 0x74, 0x53, 0xCD, 0xE6, 0xD7, 0x7B, 0x0B, 0x2A];

    private static ReadOnlySpan<byte> AlternateModernLoaderMagic
        => [0x6E, 0x53, 0x35, 0x57, 0x37, 0x64, 0x54, 0x83, 0xAA, 0x1B, 0x0F, 0x6A];

    public static (InnoParsedHeader Header, InnoLoaderOffsets Offsets)? Read(
        Stream stream,
        InnoProbeOptions options)
    {
        InnoLoaderOffsets? offsets = FindOffsets(stream, options);
        if (offsets is null)
        {
            return null;
        }

        if (offsets.HeaderOffset < 0 || offsets.HeaderOffset + 64 > stream.Length)
        {
            throw new InvalidDataException("The Inno Setup loader points to a truncated setup header.");
        }

        stream.Position = offsets.HeaderOffset;
        byte[] versionBytes = new byte[64];
        stream.ReadExactly(versionBytes);
        (Version version, bool unicode) = ParseVersion(versionBytes);
        if (version >= new Version(6, 7, 0))
        {
            byte encryptionUse = ReadEncryptionHeader(stream);
            offsets = offsets with { PayloadEncrypted = encryptionUse != 0 };
        }

        byte[] header = ReadBlock(stream, version, options);
        return (ParseHeader(header, version, unicode, options), offsets);
    }

    private static InnoLoaderOffsets? FindOffsets(Stream stream, InnoProbeOptions options)
    {
        Span<byte> legacyPointer = stackalloc byte[12];
        if (TryReadAt(stream, 0x30, legacyPointer)
            && BinaryPrimitives.ReadUInt32LittleEndian(legacyPointer) == 0x6F6E6E49)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(legacyPointer[4..]);
            uint complement = BinaryPrimitives.ReadUInt32LittleEndian(legacyPointer[8..]);
            if (offset != ~complement)
            {
                throw new InvalidDataException("The Inno Setup loader offset complement is invalid.");
            }

            return ReadOffsetTable(stream, offset);
        }

        int scanLength = (int)Math.Min(stream.Length, options.MaximumLoaderScanBytes);
        byte[] scan = new byte[scanLength];
        stream.Position = 0;
        stream.ReadExactly(scan);
        int searchOffset = 0;
        while (searchOffset <= scan.Length - ModernLoaderMagic.Length)
        {
            int first = scan.AsSpan(searchOffset).IndexOf(ModernLoaderMagic);
            int second = scan.AsSpan(searchOffset).IndexOf(AlternateModernLoaderMagic);
            if (first < 0 && second < 0)
            {
                break;
            }

            int relative = first < 0
                ? second
                : second < 0
                    ? first
                    : Math.Min(first, second);
            int candidate = searchOffset + relative;
            if (!IsPlausibleLoaderTable(scan.AsSpan(candidate)))
            {
                searchOffset = candidate + 1;
                continue;
            }

            // A structurally plausible loader table claims this executable. Surface corruption
            // instead of silently classifying a damaged Inno installer as generic.
            return ReadOffsetTable(stream, candidate);
        }

        return null;
    }

    private static bool IsPlausibleLoaderTable(ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length < 16
            || !(candidate[..12].SequenceEqual(ModernLoaderMagic)
                || candidate[..12].SequenceEqual(AlternateModernLoaderMagic)))
        {
            return false;
        }

        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..]);
        if (revision == 1)
        {
            return true;
        }

        return revision switch
        {
            2 when candidate.Length >= 64
                => Crc32(candidate[..60]) == BinaryPrimitives.ReadUInt32LittleEndian(candidate[60..]),
            _ when candidate.Length >= 44
                => Crc32(candidate[..40]) == BinaryPrimitives.ReadUInt32LittleEndian(candidate[40..]),
            _ => false,
        };
    }

    private static InnoLoaderOffsets ReadOffsetTable(Stream stream, long offset)
    {
        Span<byte> prefix = stackalloc byte[16];
        if (!TryReadAt(stream, offset, prefix))
        {
            throw new InvalidDataException("The Inno Setup loader offset table is truncated.");
        }

        bool modern = prefix[..12].SequenceEqual(ModernLoaderMagic)
            || prefix[..12].SequenceEqual(AlternateModernLoaderMagic);
        if (!modern)
        {
            throw new InvalidDataException("The Inno Setup loader uses an unsupported loader-header family.");
        }

        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
        return revision switch
        {
            1 => ReadRevision1OffsetTable(stream, offset),
            2 => ReadRevision2OffsetTable(stream, offset),
            _ => throw new InvalidDataException($"The Inno Setup loader revision {revision} is not supported."),
        };
    }

    private static InnoLoaderOffsets ReadRevision1OffsetTable(Stream stream, long offset)
    {
        Span<byte> table = stackalloc byte[44];
        if (!TryReadAt(stream, offset, table))
        {
            throw new InvalidDataException("The Inno Setup loader offset table is truncated.");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(table[40..]);
        if (Crc32(table[..40]) != expectedCrc)
        {
            throw new InvalidDataException("The Inno Setup loader offset-table checksum is invalid.");
        }

        uint headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(table[32..]);
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(table[36..]);
        if (headerOffset == 0 || headerOffset >= stream.Length || dataOffset > stream.Length)
        {
            throw new InvalidDataException(
                "The Inno Setup loader contains an out-of-range data offset; the installer is truncated or malformed.");
        }

        return new InnoLoaderOffsets(headerOffset, dataOffset);
    }

    private static InnoLoaderOffsets ReadRevision2OffsetTable(Stream stream, long offset)
    {
        Span<byte> table = stackalloc byte[64];
        if (!TryReadAt(stream, offset, table))
        {
            throw new InvalidDataException("The Inno Setup loader revision-2 offset table is truncated.");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(table[60..]);
        if (Crc32(table[..60]) != expectedCrc)
        {
            throw new InvalidDataException("The Inno Setup revision-2 loader offset-table checksum is invalid.");
        }

        long headerOffset = BinaryPrimitives.ReadInt64LittleEndian(table[40..]);
        long dataOffset = BinaryPrimitives.ReadInt64LittleEndian(table[48..]);
        if (headerOffset <= 0 || headerOffset >= stream.Length || dataOffset < 0 || dataOffset > stream.Length)
        {
            throw new InvalidDataException(
                "The Inno Setup revision-2 loader contains an out-of-range data offset; the installer is truncated or malformed.");
        }

        return new InnoLoaderOffsets(headerOffset, dataOffset);
    }

    private static (Version Version, bool Unicode) ParseVersion(byte[] bytes)
    {
        int terminator = Array.IndexOf(bytes, (byte)0);
        string text = Encoding.ASCII.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
        if (!text.Contains("Inno Setup Setup Data", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Inno Setup setup-data version identifier is invalid.");
        }

        Match match = SetupVersionRegex().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out int major)
            || !int.TryParse(match.Groups[2].Value, out int minor)
            || !int.TryParse(match.Groups[3].Value, out int build)
            || (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out _)))
        {
            throw new InvalidDataException($"The Inno Setup setup-data version \"{text}\" is not understood.");
        }

        int revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        var version = match.Groups[4].Success
            ? new Version(major, minor, build, revision)
            : new Version(major, minor, build);
        if (version < new Version(5, 5, 7))
        {
            throw new UnsupportedInnoVersionException(version, isFuture: false);
        }

        if (version > new Version(7, 0, 0, 3))
        {
            throw new UnsupportedInnoVersionException(version, isFuture: true);
        }

        bool unicode = version >= new Version(6, 3, 0)
            || text.Contains("(u)", StringComparison.OrdinalIgnoreCase);
        return (version, unicode);
    }

    private static byte ReadEncryptionHeader(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[53];
        try
        {
            stream.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The Inno Setup encryption header is truncated.", exception);
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        ReadOnlySpan<byte> header = bytes[4..];
        if (Crc32(header) != expectedCrc)
        {
            throw new InvalidDataException("The Inno Setup encryption-header checksum is invalid.");
        }

        byte encryptionUse = header[0];
        if (encryptionUse > 2)
        {
            throw new InvalidDataException($"The Inno Setup encryption mode {encryptionUse} is invalid.");
        }

        if (encryptionUse == 2)
        {
            throw new UnsupportedInnoEncryptionException();
        }

        return encryptionUse;
    }

    private static byte[] ReadBlock(Stream stream, Version version, InnoProbeOptions options)
    {
        int blockHeaderSize = version >= new Version(6, 7, 0) ? 13 : 9;
        Span<byte> blockHeader = stackalloc byte[13];
        blockHeader = blockHeader[..blockHeaderSize];
        try
        {
            stream.ReadExactly(blockHeader);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The Inno Setup primary header block is truncated.", exception);
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
        long storedSize = version >= new Version(6, 7, 0)
            ? BinaryPrimitives.ReadInt64LittleEndian(blockHeader[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[4..]);
        byte compressed = blockHeader[^1];
        if (Crc32(blockHeader[4..]) != expectedCrc)
        {
            throw new InvalidDataException("The Inno Setup primary header block checksum is invalid.");
        }

        if (storedSize <= 0 || storedSize > options.MaximumStoredHeaderBytes)
        {
            throw new InvalidDataException(
                $"The Inno Setup stored header size {storedSize} is outside the configured limit.");
        }

        if (compressed > 1 || storedSize > stream.Length - stream.Position)
        {
            throw new InvalidDataException("The Inno Setup primary header block is malformed or truncated.");
        }

        byte[] framed = new byte[checked((int)storedSize)];
        stream.ReadExactly(framed);
        byte[] packed = RemoveChunkChecksums(framed);
        if (compressed == 0)
        {
            if (packed.Length > options.MaximumExpandedHeaderBytes)
            {
                throw new InvalidDataException("The expanded Inno Setup header exceeds the configured limit.");
            }

            return packed;
        }

        try
        {
            using var input = new MemoryStream(packed, writable: false);
            using Stream decoder = CreateLzma1(input, options.MaximumLzmaDictionaryBytes);
            return ReadToLimit(decoder, options.MaximumExpandedHeaderBytes, "Inno Setup header");
        }
        catch (Exception exception) when (exception is SharpCompressException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException("The Inno Setup primary header cannot be decompressed.", exception);
        }
    }

    private static byte[] RemoveChunkChecksums(byte[] framed)
    {
        using var output = new MemoryStream(framed.Length);
        int offset = 0;
        while (offset < framed.Length)
        {
            int remaining = framed.Length - offset;
            if (remaining < 5)
            {
                throw new InvalidDataException("The Inno Setup header has a truncated checksum frame.");
            }

            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(framed.AsSpan(offset));
            offset += 4;
            int length = Math.Min(4096, framed.Length - offset);
            ReadOnlySpan<byte> chunk = framed.AsSpan(offset, length);
            if (Crc32(chunk) != expected)
            {
                throw new InvalidDataException("The Inno Setup header chunk checksum is invalid.");
            }

            output.Write(chunk);
            offset += length;
        }

        return output.ToArray();
    }

    private static InnoParsedHeader ParseHeader(
        byte[] data,
        Version version,
        bool unicode,
        InnoProbeOptions options)
    {
        InnoParsedHeader parsed = ParseHeaderWithCodePage(data, version, unicode, options, 1252);
        if (unicode || parsed.Languages.Count == 0)
        {
            return parsed;
        }

        uint codePage = parsed.Languages.Any(language => language.CodePage == 1252)
            ? 1252
            : parsed.Languages[0].CodePage;
        if (codePage == 1252)
        {
            return parsed;
        }

        if (codePage > int.MaxValue || !IsSupportedCodePage((int)codePage))
        {
            return parsed with
            {
                Diagnostics =
                [
                    .. parsed.Diagnostics,
                    new AnalysisDiagnostic(
                        "INNO005",
                        $"The Inno Setup ANSI code page {codePage} is not supported; metadata was decoded with replacement characters using Windows-1252.",
                        RequiresManualAnalysis: true),
                ],
            };
        }

        return ParseHeaderWithCodePage(data, version, unicode, options, (int)codePage);
    }

    private static InnoParsedHeader ParseHeaderWithCodePage(
        byte[] data,
        Version version,
        bool unicode,
        InnoProbeOptions options,
        int ansiCodePage)
    {
        var reader = new InnoBinaryReader(data, unicode, ansiCodePage, options);
        string? appName = reader.ReadString();
        string? appVerName = reader.ReadString();
        string? appId = reader.ReadString();
        _ = reader.ReadString(); // AppCopyright
        string? publisher = reader.ReadString();
        _ = reader.ReadString(); // AppPublisherURL
        _ = reader.ReadString(); // AppSupportPhone
        _ = reader.ReadString(); // AppSupportURL
        _ = reader.ReadString(); // AppUpdatesURL
        string? appVersion = reader.ReadString();
        string? defaultDirName = reader.ReadString();
        _ = reader.ReadString(); // DefaultGroupName
        _ = reader.ReadString(); // BaseFilename
        _ = reader.ReadString(); // UninstallFilesDir
        string? uninstallDisplayName = reader.ReadString();
        _ = reader.ReadString(); // UninstallIcon
        _ = reader.ReadString(); // AppMutex
        _ = reader.ReadString(); // DefaultUserName
        _ = reader.ReadString(); // DefaultUserOrganisation
        _ = reader.ReadString(); // DefaultSerial
        _ = reader.ReadString(); // AppReadmeFile
        _ = reader.ReadString(); // AppContact
        _ = reader.ReadString(); // AppComments
        _ = reader.ReadString(); // AppModifyPath
        string? createUninstallRegKey = reader.ReadString();
        string? uninstallable = reader.ReadString();
        _ = reader.ReadString(); // CloseApplicationsFilter
        _ = reader.ReadString(); // SetupMutex
        if (version >= new Version(5, 6, 1))
        {
            _ = reader.ReadString(); // ChangesEnvironment
            _ = reader.ReadString(); // ChangesAssociations
        }

        string? architecturesAllowed = null;
        string? architectures64 = null;
        if (version >= new Version(6, 3, 0))
        {
            architecturesAllowed = reader.ReadString();
            architectures64 = reader.ReadString();
        }

        if (version >= new Version(6, 4, 3))
        {
            _ = reader.ReadString(); // CloseApplicationsFilterExcludes
        }

        if (version >= new Version(6, 7, 0))
        {
            reader.SkipString(); // SevenZipLibraryName
            reader.SkipString(); // UsePreviousAppDir
            reader.SkipString(); // UsePreviousGroup
            reader.SkipString(); // UsePreviousSetupType
            reader.SkipString(); // UsePreviousTasks
            reader.SkipString(); // UsePreviousUserInfo
        }

        reader.SkipString(ansi: true); // LicenseText
        reader.SkipString(ansi: true); // InfoBefore
        reader.SkipString(ansi: true); // InfoAfter
        reader.SkipLengthPrefixedBytes(options.MaximumCompiledCodeBytes, "compiled code");

        if (!unicode)
        {
            reader.Skip(32); // DBCS lead-byte bitset.
        }

        uint languageCount = reader.ReadUInt32();
        if (languageCount > options.MaximumLanguages)
        {
            throw new InvalidDataException(
                $"The Inno Setup language count {languageCount} exceeds the configured limit.");
        }

        reader.Skip(4 * 5); // message, permission, type, component and task counts.
        reader.Skip(version >= new Version(6, 7, 0) ? 4 * 11 : 4 * 10); // directory through uninstall-run counts.
        if (version >= new Version(7, 0, 0, 3))
        {
            reader.Skip(4); // CompiledCodeVersion.
        }

        reader.Skip(20); // Minimum and maximum Windows versions.

        if (version < new Version(6, 4, 0, 1))
        {
            reader.Skip(8); // BackColor and BackColor2.
        }

        if (version >= new Version(6, 7, 0))
        {
            reader.Skip(8); // Wizard resize percentages.
            reader.Skip(1); // WizardDarkStyle.
            reader.Skip(1); // WizardImageAlphaFormat.
            reader.Skip(24); // Light and dynamic-dark wizard colors.
            reader.Skip(2); // Wizard image and background opacity.
            reader.Skip(1); // WizardLightControlStyling.
        }
        else
        {
            if (version >= new Version(6, 0, 0))
            {
                reader.Skip(9); // WizardStyle and resize percentages.
            }

            reader.Skip(1); // WizardImageAlphaFormat.
            reader.Skip(version >= new Version(6, 4, 0) ? 4 + 44 : 20 + 8);
        }

        reader.Skip(12); // ExtraDiskSpaceRequired and SlicesPerDisk.
        reader.Skip(1); // UninstallLogMode.
        reader.Skip(1); // DirExistsWarning.

        byte privilegeValue = reader.ReadByte();
        InnoPrivilegeLevel privileges = privilegeValue switch
        {
            0 => InnoPrivilegeLevel.None,
            1 => InnoPrivilegeLevel.PowerUser,
            2 => InnoPrivilegeLevel.Admin,
            3 => InnoPrivilegeLevel.Lowest,
            _ => throw new InvalidDataException($"The Inno Setup privilege value {privilegeValue} is invalid."),
        };

        byte privilegeOverrides = version >= new Version(5, 7, 0) ? reader.ReadByte() : (byte)0;
        reader.Skip(2); // ShowLanguageDialog and LanguageDetectionMethod.
        byte compressionValue = reader.ReadByte();
        InnoCompression compression = compressionValue switch
        {
            0 => InnoCompression.Stored,
            1 => InnoCompression.Zlib,
            2 => InnoCompression.Bzip2,
            3 => InnoCompression.Lzma1,
            4 => InnoCompression.Lzma2,
            _ => throw new InvalidDataException($"The Inno Setup compression value {compressionValue} is invalid."),
        };

        bool hasUnsupportedIa64 = false;
        if (version < new Version(6, 3, 0))
        {
            (architecturesAllowed, hasUnsupportedIa64) = FormatArchitectureFlags(reader.ReadByte());
            (architectures64, bool modeHasUnsupportedIa64) = FormatArchitectureFlags(reader.ReadByte());
            hasUnsupportedIa64 |= modeHasUnsupportedIa64;
        }

        reader.Skip(2); // DisableDirPage and DisableProgramGroupPage.
        reader.Skip(8); // UninstallDisplaySize.
        reader.Skip(GetSetupFlagByteCount(version, unicode));

        List<InnoLanguage> languages = [];
        for (int i = 0; i < languageCount; i++)
        {
            languages.Add(ReadLanguage(reader, version, unicode));
        }

        List<AnalysisDiagnostic> diagnostics = [];
        if (reader.HadDecodingReplacement)
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO004",
                $"One or more Inno Setup ANSI strings contain bytes that are undefined in code page {ansiCodePage}; replacement characters were retained instead of aborting analysis."));
        }
        if (hasUnsupportedIa64)
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO006",
                "The legacy Inno Setup architecture flags include IA64, which WinGet cannot represent as an installer architecture. Architecture requires manual analysis.",
                RequiresManualAnalysis: true));
        }

        return new InnoParsedHeader(
            version,
            unicode,
            NullIfEmpty(appName),
            NullIfEmpty(appVerName),
            NullIfEmpty(appId),
            NullIfEmpty(appVersion),
            NullIfEmpty(publisher),
            NullIfEmpty(defaultDirName),
            NullIfEmpty(uninstallDisplayName),
            NullIfEmpty(createUninstallRegKey),
            NullIfEmpty(uninstallable),
            NullIfEmpty(architecturesAllowed),
            NullIfEmpty(architectures64),
            privileges,
            privilegeOverrides != 0,
            compression,
            languages,
            hasUnsupportedIa64,
            diagnostics);
    }

    private static InnoLanguage ReadLanguage(InnoBinaryReader reader, Version version, bool unicode)
    {
        string? name = reader.ReadString();
        _ = reader.ReadString(); // LanguageName
        reader.SkipString(); // DialogFont
        if (version < new Version(6, 7, 0))
        {
            reader.SkipString(); // TitleFont
        }

        reader.SkipString(); // WelcomeFont
        if (version < new Version(6, 7, 0))
        {
            reader.SkipString(); // CopyrightFont
        }

        reader.SkipString(); // Data
        reader.SkipString(); // LicenseText
        reader.SkipString(); // InfoBefore
        reader.SkipString(); // InfoAfter
        uint languageId = version >= new Version(6, 7, 0) ? reader.ReadUInt16() : reader.ReadUInt32();
        uint codePage = unicode ? 1200u : reader.ReadUInt32();
        if (codePage == 0)
        {
            codePage = 1252;
        }

        reader.Skip(16); // Font sizes and, in 6.7+, dialog base-scale dimensions.
        reader.Skip(1); // RightToLeft.
        LanguageTag? locale = languageId <= ushort.MaxValue ? Lcid.ToLanguageTag((ushort)languageId) : null;
        return new InnoLanguage(NullIfEmpty(name), languageId, codePage, locale);
    }

    private static int GetSetupFlagByteCount(Version version, bool unicode)
    {
        if (version >= new Version(6, 7, 0))
        {
            return 8;
        }

        int count = 0;
        Add(1); // DisableStartupPrompt
        Add(1); // CreateAppDir
        Add(1); // AllowNoIcons
        Add(1); // AlwaysRestart
        Add(1); // AlwaysUsePersonalGroup
        if (version < new Version(6, 4, 0, 1))
        {
            Add(4); // Window flags.
        }

        Add(1); // EnableDirDoesntExistWarning
        Add(1); // Password
        Add(1); // AllowRootDirectory
        Add(1); // DisableFinishedPage
        if (version < new Version(5, 6, 1))
        {
            Add(1); // ChangesAssociations
        }

        Add(1); // UsePreviousAppDir
        if (version < new Version(6, 4, 0, 1))
        {
            Add(1); // BackColorHorizontal
        }

        Add(1); // UsePreviousGroup
        Add(1); // UpdateUninstallLogAppName
        Add(1); // UsePreviousSetupType
        Add(6); // Ready/components/task flags.
        Add(2); // AlwaysShowDir/GroupOnReadyPage
        Add(1); // AllowUNCPath
        Add(2); // UserInfoPage, UsePreviousUserInfo
        Add(1); // UninstallRestartComputer
        Add(1); // RestartIfNeededByRun
        Add(1); // ShowTasksTreeLines
        Add(1); // AllowCancelDuringInstall
        Add(1); // WizardImageStretch
        Add(2); // AppendDefaultDirName/GroupName
        Add(1); // EncryptionUsed
        if (version < new Version(5, 6, 1))
        {
            Add(1); // ChangesEnvironment
        }

        if (!unicode)
        {
            Add(1); // ShowUndisplayableLanguages
        }

        Add(1); // SetupLogging
        Add(1); // SignedUninstaller
        Add(1); // UsePreviousLanguage
        Add(1); // DisableWelcomePage
        Add(3); // CloseApplications, RestartApplications, AllowNetworkDrive
        Add(1); // ForceCloseApplications
        if (version >= new Version(6, 0, 0))
        {
            Add(3); // AppNameHasConsts, UsePreviousPrivileges, WizardResizable
        }

        if (version >= new Version(6, 3, 0))
        {
            Add(1); // UninstallLogging
        }

        int bytes = (count + 7) / 8;
        return bytes == 3 ? 4 : bytes;

        void Add(int amount) => count += amount;
    }

    private static (string? Expression, bool HasUnsupportedIa64) FormatArchitectureFlags(byte flags)
    {
        List<string> values = [];
        if ((flags & 0x02) != 0)
        {
            values.Add("x86os");
        }

        if ((flags & 0x04) != 0)
        {
            values.Add("x64os");
        }

        bool hasUnsupportedIa64 = (flags & 0x08) != 0;
        if ((flags & 0x08) != 0)
        {
            values.Add("ia64");
        }

        if ((flags & 0x10) != 0)
        {
            values.Add("arm64");
        }

        return (values.Count == 0 ? null : string.Join(" or ", values), hasUnsupportedIa64);
    }

    public static InnoPayloadScanResult InspectPayloads(
        Stream stream,
        InnoLoaderOffsets offsets,
        InnoCompression compression,
        InnoProbeOptions options)
    {
        if (offsets.PayloadEncrypted)
        {
            return new InnoPayloadScanResult(
                [],
                [
                    new AnalysisDiagnostic(
                        "INNO014",
                        "The Inno Setup file payload is encrypted; embedded architecture and dependency evidence is unavailable without the password.",
                        RequiresManualAnalysis: true),
                ],
                IsComplete: false);
        }

        long start = offsets.DataOffset > 0 ? offsets.DataOffset : offsets.HeaderOffset;
        long available = Math.Max(0, stream.Length - start);
        int scanLimit = Math.Min(options.MaximumPayloadScanBytes, options.MaximumAggregatePayloadBytes);
        int length = (int)Math.Min(
            available,
            scanLimit);
        var diagnostics = new List<AnalysisDiagnostic>();
        bool complete = available <= scanLimit;
        if (!complete)
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO007",
                $"Inno Setup payload inspection read {scanLimit} bytes from a {available}-byte payload region; architecture evidence outside the configured scan window is unavailable.",
                RequiresManualAnalysis: true));
        }

        if (length == 0)
        {
            return new InnoPayloadScanResult([], diagnostics, complete);
        }

        var budget = new PayloadInspectionBudget(options.MaximumAggregatePayloadBytes);
        byte[] data = new byte[length];
        stream.Position = start;
        stream.ReadExactly(data);
        budget.Consume(length);
        List<InnoPayloadCandidate> result = FindPeImages(
            data,
            options.MaximumPayloadCandidates,
            out bool candidateLimitExceeded);

        if (compression == InnoCompression.Bzip2)
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO003",
                "The Inno Setup payload uses bzip2 compression, which is not supported by the bounded payload inspector. Embedded architecture evidence is unavailable.",
                RequiresManualAnalysis: true));
            if (candidateLimitExceeded)
            {
                diagnostics.Add(new AnalysisDiagnostic(
                    "INNO013",
                    $"Inno Setup payload inspection found more than {options.MaximumPayloadCandidates} valid PE candidates; remaining architecture evidence was skipped.",
                    RequiresManualAnalysis: true));
            }
            return new InnoPayloadScanResult(result, diagnostics, IsComplete: false);
        }

        ReadOnlySpan<byte> magic = "zlb\x1a"u8;
        int search = 0;
        int attempts = 0;
        while (search <= data.Length - magic.Length
               && attempts < options.MaximumPayloadMarkerAttempts
               && budget.Remaining >= 2
               && result.Count < options.MaximumPayloadCandidates)
        {
            int relative = data.AsSpan(search).IndexOf(magic);
            if (relative < 0)
            {
                break;
            }

            int payloadOffset = search + relative + magic.Length;
            attempts++;
            TryInspectCompressedPayload(
                data,
                payloadOffset,
                compression,
                options,
                budget,
                result,
                out bool budgetExceeded,
                out bool compressedCandidateLimitExceeded);
            if (budgetExceeded)
            {
                complete = false;
            }
            candidateLimitExceeded |= compressedCandidateLimitExceeded;

            search = payloadOffset;
        }

        bool hasUnprocessedMarker = search <= data.Length - magic.Length
            && data.AsSpan(search).IndexOf(magic) >= 0;
        if (hasUnprocessedMarker && attempts == options.MaximumPayloadMarkerAttempts)
        {
            complete = false;
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO008",
                $"Inno Setup payload inspection stopped after {options.MaximumPayloadMarkerAttempts} compressed-payload markers.",
                RequiresManualAnalysis: true));
        }
        else if (hasUnprocessedMarker && result.Count >= options.MaximumPayloadCandidates)
        {
            candidateLimitExceeded = true;
        }
        else if (hasUnprocessedMarker && budget.Remaining < 2)
        {
            complete = false;
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO009",
                "The Inno Setup aggregate payload-inspection budget was exhausted before every compressed payload marker could be checked.",
                RequiresManualAnalysis: true));
        }
        else if (!complete
            && diagnostics.All(static diagnostic => diagnostic.Code is not "INNO007" and not "INNO009"))
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO009",
                "The Inno Setup aggregate payload-inspection budget was exhausted before every candidate could be checked.",
                RequiresManualAnalysis: true));
        }

        if (candidateLimitExceeded)
        {
            complete = false;
            diagnostics.Add(new AnalysisDiagnostic(
                "INNO013",
                $"Inno Setup payload inspection found more than {options.MaximumPayloadCandidates} valid PE candidates; remaining architecture evidence was skipped.",
                RequiresManualAnalysis: true));
        }

        return new InnoPayloadScanResult(result, diagnostics, complete);
    }

    private static void TryInspectCompressedPayload(
        byte[] data,
        int offset,
        InnoCompression compression,
        InnoProbeOptions options,
        PayloadInspectionBudget budget,
        List<InnoPayloadCandidate> result,
        out bool budgetExceeded,
        out bool candidateLimitExceeded)
    {
        budgetExceeded = false;
        candidateLimitExceeded = false;
        int inputLimit = Math.Min(data.Length - offset, budget.Remaining / 2);
        int outputLimit = Math.Min(options.MaximumExpandedPayloadBytes, budget.Remaining - inputLimit);
        if (inputLimit <= 0 || outputLimit <= 0)
        {
            budgetExceeded = true;
            return;
        }

        using var input = new PayloadInputStream(data, offset, inputLimit);
        int expandedCharge = 0;
        try
        {
            using Stream decoded = compression switch
            {
                InnoCompression.Stored => input,
                InnoCompression.Zlib => new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true),
                InnoCompression.Lzma1 => CreateLzma1(input, options.MaximumLzmaDictionaryBytes),
                InnoCompression.Lzma2 => CreateLzma2(input, options.MaximumLzmaDictionaryBytes),
                _ => throw new NotSupportedException(),
            };
            byte[] expanded = ReadToLimit(
                decoded,
                outputLimit,
                "Inno Setup payload",
                bytesRead => expandedCharge = bytesRead);
            expandedCharge = expanded.Length;
            result.AddRange(FindPeImages(
                expanded,
                options.MaximumPayloadCandidates - result.Count,
                out candidateLimitExceeded));
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or EndOfStreamException
                or SharpCompressException
                or ArgumentException
                or NotSupportedException)
        {
            // A zlb marker can occur in compressed bytes. Payload evidence is optional; a
            // candidate that does not decode is ignored rather than invalidating the header.
            budgetExceeded = exception is InvalidDataException
                && exception.Message.Contains("exceeds the configured limit", StringComparison.Ordinal);
        }
        finally
        {
            long processed = Math.Max(1, input.BytesRead + expandedCharge);
            budget.Consume((int)Math.Min(processed, budget.Remaining));
        }
    }

    private static List<InnoPayloadCandidate> FindPeImages(
        byte[] data,
        int maximum,
        out bool limitExceeded)
    {
        limitExceeded = false;
        List<InnoPayloadCandidate> result = [];
        for (int i = 0; i <= data.Length - 64; i++)
        {
            if (data[i] != (byte)'M' || data[i + 1] != (byte)'Z')
            {
                continue;
            }

            if (!TryInspectPe(data, i, out Architecture architecture, out long imageSize))
            {
                continue;
            }

            if (result.Count >= maximum)
            {
                limitExceeded = true;
                break;
            }

            using var image = new MemoryStream(
                data,
                i,
                checked((int)imageSize),
                writable: false,
                publiclyVisible: true);
            PeImportInspection imports = PeImportReader.Inspect(
                image,
                PayloadDependencyAnalyzerOptions.DefaultMaximumImportDescriptors,
                PayloadDependencyAnalyzerOptions.DefaultMaximumImportNameBytes);
            result.Add(new InnoPayloadCandidate(architecture, imageSize, imports));
            i += 63;
        }

        return result;
    }

    private static bool TryInspectPe(
        byte[] data,
        int imageOffset,
        out Architecture architecture,
        out long imageSize)
    {
        architecture = default;
        imageSize = 0;
        int available = data.Length - imageOffset;
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(imageOffset + 0x3C));
        long signature = imageOffset + (long)peOffset;
        if (peOffset < 64
            || signature + 24 > data.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)signature)) != 0x00004550)
        {
            return false;
        }

        Machine machine = (Machine)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)signature + 4));
        architecture = machine switch
        {
            Machine.I386 => Architecture.X86,
            Machine.Amd64 => Architecture.X64,
            Machine.Arm64 => Architecture.Arm64,
            Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => Architecture.Arm,
            _ => (Architecture)(-1),
        };
        if ((int)architecture < 0)
        {
            return false;
        }

        int sections = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)signature + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)signature + 20));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)signature + 22));
        long optionalOffset = signature + 24;
        long table = optionalOffset + optionalSize;
        if (sections is < 1 or > 96
            || optionalSize is < 64 or > 4096
            || table + (sections * 40L) > data.Length
            || (characteristics & (ushort)Characteristics.ExecutableImage) == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> optional = data.AsSpan((int)optionalOffset, optionalSize);
        ushort optionalMagic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        bool expectedPe32Plus = machine is Machine.Amd64 or Machine.Arm64;
        if ((expectedPe32Plus && optionalMagic != 0x20B)
            || (!expectedPe32Plus && optionalMagic != 0x10B)
            || (optionalMagic == 0x10B && optionalSize < 96)
            || (optionalMagic == 0x20B && optionalSize < 112))
        {
            return false;
        }

        uint sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(optional[32..]);
        uint fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(optional[36..]);
        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(optional[56..]);
        uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(optional[60..]);
        long relativeTableEnd = table + (sections * 40L) - imageOffset;
        if (sectionAlignment == 0
            || fileAlignment == 0
            || sizeOfImage < sizeOfHeaders
            || sizeOfImage % sectionAlignment != 0
            || sizeOfHeaders < relativeTableEnd
            || sizeOfHeaders > available)
        {
            return false;
        }

        long rawEnd = sizeOfHeaders;
        bool hasRawSection = false;
        for (int section = 0; section < sections; section++)
        {
            int entry = (int)table + (section * 40);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 16));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 20));
            long virtualEnd = virtualAddress + (long)Math.Max(virtualSize, rawSize);
            long sectionRawEnd = rawOffset + (long)rawSize;
            if (virtualAddress >= sizeOfImage
                || virtualEnd > sizeOfImage
                || (rawSize > 0 && (rawOffset < sizeOfHeaders || sectionRawEnd > available)))
            {
                return false;
            }

            hasRawSection |= rawSize > 0;
            rawEnd = Math.Max(rawEnd, sectionRawEnd);
        }

        if (!hasRawSection)
        {
            return false;
        }

        imageSize = rawEnd;
        return true;
    }

    private static LzmaStream CreateLzma1(Stream input, int maximumDictionaryBytes)
    {
        byte[] properties = new byte[5];
        input.ReadExactly(properties);
        int property = properties[0];
        int lc = property % 9;
        int remainder = property / 9;
        int lp = remainder % 5;
        int pb = remainder / 5;
        uint dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
        if (property >= 9 * 5 * 5 || lc + lp > 4 || pb > 4)
        {
            throw new InvalidDataException("The Inno Setup LZMA1 properties are invalid.");
        }

        ValidateLzmaDictionary(dictionarySize, maximumDictionaryBytes);
        return LzmaStream.Create(properties, input, leaveOpen: false);
    }

    private static LzmaStream CreateLzma2(Stream input, int maximumDictionaryBytes)
    {
        int property = input.ReadByte();
        if (property is < 0 or > 40)
        {
            throw new InvalidDataException("The Inno Setup LZMA2 property is invalid.");
        }

        uint dictionarySize = property == 40
            ? uint.MaxValue
            : (uint)((2 | (property & 1)) << ((property / 2) + 11));
        ValidateLzmaDictionary(dictionarySize, maximumDictionaryBytes);
        return LzmaStream.Create(
            [(byte)property],
            input,
            inputSize: -1,
            outputSize: -1,
            presetDictionary: null!,
            isLzma2: true,
            leaveOpen: false);
    }

    private static void ValidateLzmaDictionary(uint dictionarySize, int maximumDictionaryBytes)
    {
        if (dictionarySize == 0 || dictionarySize > maximumDictionaryBytes)
        {
            throw new InvalidDataException(
                $"The Inno Setup LZMA dictionary size {dictionarySize} exceeds the configured limit.");
        }
    }

    private static byte[] ReadToLimit(
        Stream input,
        int maximum,
        string name,
        Action<int>? reportBytesRead = null)
    {
        using var output = new MemoryStream(Math.Min(maximum, 64 * 1024));
        byte[] buffer = new byte[81920];
        while (true)
        {
            int allowed = Math.Min(buffer.Length, maximum + 1 - (int)output.Length);
            if (allowed <= 0)
            {
                reportBytesRead?.Invoke((int)output.Length);
                throw new InvalidDataException($"The expanded {name} exceeds the configured limit.");
            }

            int read = input.Read(buffer, 0, allowed);
            if (read == 0)
            {
                reportBytesRead?.Invoke((int)output.Length);
                return output.ToArray();
            }

            output.Write(buffer, 0, read);
            reportBytesRead?.Invoke((int)output.Length);
        }
    }

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }

        return ~crc;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsSupportedCodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            _ = Encoding.GetEncoding(codePage);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\((\d+)\.(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex SetupVersionRegex();
}

internal sealed class PayloadInspectionBudget
{
    public PayloadInspectionBudget(int maximumBytes)
    {
        Remaining = maximumBytes;
    }

    public int Remaining { get; private set; }

    public void Consume(int bytes)
    {
        if (bytes < 0 || bytes > Remaining)
        {
            throw new InvalidOperationException("The Inno Setup payload inspection budget was exceeded.");
        }

        Remaining -= bytes;
    }
}

internal sealed class PayloadInputStream : Stream
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;

    public PayloadInputStream(byte[] data, int offset, int length)
    {
        _data = data;
        _position = offset;
        _end = offset + length;
    }

    public int BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _end - (_position - BytesRead);

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, _end - _position);
        if (count <= 0)
        {
            return 0;
        }

        _data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        BytesRead += count;
        return count;
    }

    public override int ReadByte()
    {
        if (_position >= _end)
        {
            return -1;
        }

        BytesRead++;
        return _data[_position++];
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class InnoBinaryReader
{
    private readonly byte[] _data;
    private readonly Encoding _encoding;
    private readonly InnoProbeOptions _options;
    private int _position;
    private int _totalStringBytes;

    public bool HadDecodingReplacement { get; private set; }

    public InnoBinaryReader(byte[] data, bool unicode, int ansiCodePage, InnoProbeOptions options)
    {
        _data = data;
        _options = options;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (unicode)
        {
            _encoding = Encoding.Unicode;
        }
        else
        {
            _encoding = Encoding.GetEncoding(
                ansiCodePage,
                EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback("\uFFFD"));
        }
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _data[_position++];
    }

    public ushort ReadUInt16()
    {
        Ensure(2);
        ushort result = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_position));
        _position += 2;
        return result;
    }

    public uint ReadUInt32()
    {
        Ensure(4);
        uint result = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_position));
        _position += 4;
        return result;
    }

    public string? ReadString(bool ansi = false)
    {
        uint length = ReadUInt32();
        if (length > _options.MaximumStringBytes
            || length > _options.MaximumTotalStringBytes - _totalStringBytes)
        {
            throw new InvalidDataException(
                $"An Inno Setup string length {length} exceeds the configured allocation limit.");
        }

        Ensure((int)length);
        Encoding encoding = ansi
            ? Encoding.GetEncoding(
                1252,
                EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback("\uFFFD"))
            : _encoding;
        ReadOnlySpan<byte> encoded = _data.AsSpan(_position, (int)length);
        string value = encoding.GetString(encoded).TrimEnd('\0');
        if (encoding.CodePage == 1252 && ContainsUndefinedWindows1252Byte(encoded))
        {
            value = value
                .Replace('\u0081', '\uFFFD')
                .Replace('\u008D', '\uFFFD')
                .Replace('\u008F', '\uFFFD')
                .Replace('\u0090', '\uFFFD')
                .Replace('\u009D', '\uFFFD');
            HadDecodingReplacement = true;
        }

        HadDecodingReplacement |= value.Contains('\uFFFD', StringComparison.Ordinal);
        _position += (int)length;
        _totalStringBytes += (int)length;
        return value;
    }

    public void SkipString(bool ansi = false) => _ = ReadString(ansi);

    public void SkipLengthPrefixedBytes(int maximumBytes, string fieldName)
    {
        uint length = ReadUInt32();
        if (length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The Inno Setup {fieldName} length {length} exceeds the configured limit.");
        }

        Skip((int)length);
    }

    public void Skip(int count)
    {
        Ensure(count);
        _position += count;
    }

    private void Ensure(int count)
    {
        if (count < 0 || _position > _data.Length - count)
        {
            throw new InvalidDataException(
                $"The Inno Setup header is truncated at byte {_position}; {count} more bytes were required.");
        }
    }

    private static bool ContainsUndefinedWindows1252Byte(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D)
            {
                return true;
            }
        }

        return false;
    }
}
