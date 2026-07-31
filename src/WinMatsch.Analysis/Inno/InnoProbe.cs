using WinMatsch.Analysis.Pe;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Inno;

/// <summary>
/// Bounded clean-room reader for Inno Setup 5.5.7 through 6.4.0.1 setup-data families.
/// The loader offsets, checksummed header blocks and version-dependent main header are parsed
/// directly. Embedded payload PE evidence takes precedence over broad compatibility
/// expressions such as <c>x86compatible</c>.
/// </summary>
public sealed class InnoProbe : IExeFormatProbe
{
    private readonly InnoProbeOptions _options;

    public InnoProbe(InnoProbeOptions? options = null)
    {
        _options = options ?? new InnoProbeOptions();
        _options.Validate();
    }

    public InstallerAnalysis? Probe(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        InnoSetupMetadata? metadata = Inspect(peFile, stream);
        if (metadata is null)
        {
            return null;
        }

        VersionInfo version = peFile.VersionInfo;
        string? displayName = SafeArpValue(metadata.AppVerName) ?? SafeArpValue(metadata.AppName);
        string? displayVersion = SafeArpValue(metadata.AppVersion);
        string? publisher = SafeArpValue(metadata.Publisher);
        string? productCode = SafeArpValue(metadata.ProductCode);
        var installer = new Installer
        {
            Architecture = metadata.EffectiveArchitecture,
            InstallerType = InstallerType.Inno,
            Scope = metadata.Scope,
            ElevationRequirement = metadata.ElevationRequirement,
            ProductCode = productCode,
            InstallerLocale = metadata.Languages.Count == 1 ? metadata.Languages[0].Locale : null,
        };

        string? installLocation = NormalizeInstallLocation(metadata.DefaultDirName);
        if (installLocation is not null)
        {
            installer.InstallationMetadata = new InstallationMetadata { DefaultInstallLocation = installLocation };
        }

        if (displayName is not null || displayVersion is not null || publisher is not null || productCode is not null)
        {
            installer.AppsAndFeaturesEntries =
            [
                new AppsAndFeaturesEntry
                {
                    DisplayName = displayName,
                    DisplayVersion = displayVersion,
                    Publisher = publisher,
                    ProductCode = productCode,
                    InstallerType = InstallerType.Inno,
                },
            ];
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.InnoSetup,
            Installers = [installer],
            ProductName = displayName ?? version.ProductName,
            ProductVersion = displayVersion ?? version.ProductVersion,
            Publisher = publisher ?? version.CompanyName,
            Copyright = version.LegalCopyright,
        };
    }

    /// <summary>Returns detailed setup directives and payload evidence, or null for a non-Inno PE.</summary>
    public InnoSetupMetadata? Inspect(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        (InnoParsedHeader Header, InnoLoaderOffsets Offsets)? parsed = InnoFormatReader.Read(stream, _options);
        if (parsed is null)
        {
            return null;
        }

        InnoParsedHeader header = parsed.Value.Header;
        IReadOnlyList<(Architecture Architecture, long Size)> payloads =
            InnoFormatReader.InspectPayloads(stream, parsed.Value.Offsets, header.Compression, _options);
        List<Architecture> payloadArchitectures = payloads
            .OrderByDescending(payload => payload.Size)
            .Select(payload => payload.Architecture)
            .Distinct()
            .ToList();

        (Architecture? architecture, bool conclusive) = GetArchitecture(header, payloads, peFile.Architecture);
        bool overrideAllowed = header.PrivilegesMayBeOverridden;
        Scope? scope = overrideAllowed ? null : header.Privileges switch
        {
            InnoPrivilegeLevel.Admin or InnoPrivilegeLevel.PowerUser => Scope.Machine,
            InnoPrivilegeLevel.Lowest => Scope.User,
            _ => null,
        };
        ElevationRequirement? elevation = overrideAllowed ? null : header.Privileges switch
        {
            InnoPrivilegeLevel.Admin or InnoPrivilegeLevel.PowerUser => ElevationRequirement.ElevationRequired,
            InnoPrivilegeLevel.Lowest => ElevationRequirement.ElevationProhibited,
            _ => peFile.RequestedElevation,
        };

        string? appId = SafeArpValue(header.AppId);
        return new InnoSetupMetadata
        {
            SetupDataVersion = header.Version,
            IsUnicode = header.Unicode,
            AppName = header.AppName,
            AppVerName = header.AppVerName,
            AppId = appId,
            ProductCode = appId is null ? null : appId + "_is1",
            AppVersion = header.AppVersion,
            Publisher = header.Publisher,
            DefaultDirName = header.DefaultDirName,
            ArchitecturesAllowed = header.ArchitecturesAllowed,
            ArchitecturesInstallIn64BitMode = header.ArchitecturesInstallIn64BitMode,
            PrivilegesRequired = header.Privileges,
            PrivilegesMayBeOverridden = overrideAllowed,
            Scope = scope,
            ElevationRequirement = elevation,
            Languages = header.Languages,
            EmbeddedPayloadArchitectures = payloadArchitectures,
            EffectiveArchitecture = architecture,
            ArchitectureIsConclusive = conclusive,
        };
    }

    private static (Architecture? Architecture, bool Conclusive) GetArchitecture(
        InnoParsedHeader header,
        IReadOnlyList<(Architecture Architecture, long Size)> payloads,
        Architecture stubArchitecture)
    {
        if (payloads.Count > 0)
        {
            long largestSize = payloads.Max(payload => payload.Size);
            Architecture[] largest = payloads
                .Where(payload => payload.Size == largestSize)
                .Select(payload => payload.Architecture)
                .Distinct()
                .ToArray();
            if (largest.Length == 1)
            {
                return (largest[0], true);
            }
        }

        string allowed = header.ArchitecturesAllowed ?? "";
        string mode64 = header.ArchitecturesInstallIn64BitMode ?? "";
        bool arm64 = HasPositiveToken(allowed, "arm64") || HasPositiveToken(mode64, "arm64");
        bool x64 = HasPositiveToken(allowed, "x64compatible")
            || HasPositiveToken(allowed, "x64os")
            || HasPositiveToken(mode64, "x64compatible")
            || HasPositiveToken(mode64, "x64os");
        bool x86 = HasPositiveToken(allowed, "x86compatible") || HasPositiveToken(allowed, "x86os");
        int targetCount = (arm64 ? 1 : 0) + (x64 ? 1 : 0) + (x86 ? 1 : 0);
        if (targetCount == 1)
        {
            return (arm64 ? Architecture.Arm64 : x64 ? Architecture.X64 : Architecture.X86, true);
        }

        if (targetCount == 0 && stubArchitecture != Architecture.X86)
        {
            return (stubArchitecture, false);
        }

        return (null, false);
    }

    private static bool HasPositiveToken(string expression, string token)
    {
        int index = expression.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        string before = expression[..index].TrimEnd();
        return !before.EndsWith("not", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeInstallLocation(string? value)
    {
        string? safe = SafeMetadataValue(value);
        if (safe is null)
        {
            return null;
        }

        return safe
            .Replace("{autopf}", "%ProgramFiles%", StringComparison.OrdinalIgnoreCase)
            .Replace("{pf64}", "%ProgramFiles%", StringComparison.OrdinalIgnoreCase)
            .Replace("{pf32}", "%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase)
            .Replace("{pf}", "%ProgramFiles%", StringComparison.OrdinalIgnoreCase)
            .Replace("{localappdata}", "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase)
            .Replace("{userappdata}", "%APPDATA%", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeArpValue(string? value)
    {
        string? safe = SafeMetadataValue(value);
        if (safe is null
            || safe.Contains('$', StringComparison.Ordinal)
            || safe.Contains("{code:", StringComparison.OrdinalIgnoreCase)
            || safe.Contains("{param:", StringComparison.OrdinalIgnoreCase)
            || safe.Contains("{reg:", StringComparison.OrdinalIgnoreCase)
            || safe.Contains("{ini:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return safe.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : safe;
    }

    private static string? SafeMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            return null;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return value.Trim();
    }
}
