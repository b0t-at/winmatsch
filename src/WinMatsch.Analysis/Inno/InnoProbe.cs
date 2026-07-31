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
        bool canClaimArp = metadata.CreatesUninstallRegistryKey != false;
        string? displayName = canClaimArp
            ? SafeArpValue(metadata.UninstallDisplayName)
                ?? SafeArpValue(metadata.AppVerName)
                ?? SafeArpValue(metadata.AppName)
            : SafeArpValue(metadata.AppVerName) ?? SafeArpValue(metadata.AppName);
        string? displayVersion = SafeArpValue(metadata.AppVersion);
        string? publisher = SafeArpValue(metadata.Publisher);
        string? productCode = canClaimArp ? SafeArpValue(metadata.ProductCode) : null;
        var installer = new Installer
        {
            Architecture = metadata.EffectiveArchitecture,
            InstallerType = InstallerType.Inno,
            Scope = metadata.Scope,
            ElevationRequirement = metadata.ElevationRequirement,
            ProductCode = productCode,
            InstallerLocale = metadata.Languages.Count == 1 ? metadata.Languages[0].Locale : null,
        };

        string? installLocation = NormalizeInstallLocation(metadata);
        if (installLocation is not null)
        {
            installer.InstallationMetadata = new InstallationMetadata { DefaultInstallLocation = installLocation };
        }

        if (canClaimArp
            && (displayName is not null || displayVersion is not null || publisher is not null || productCode is not null))
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

        bool? createsUninstallRegistryKey = GetCreatesUninstallRegistryKey(
            header.CreateUninstallRegKey,
            header.Uninstallable);
        string? appId = SafeArpValue(header.AppId);
        return new InnoSetupMetadata
        {
            SetupDataVersion = header.Version,
            IsUnicode = header.Unicode,
            AppName = header.AppName,
            AppVerName = header.AppVerName,
            AppId = appId,
            ProductCode = appId is null || createsUninstallRegistryKey == false ? null : appId + "_is1",
            AppVersion = header.AppVersion,
            Publisher = header.Publisher,
            DefaultDirName = header.DefaultDirName,
            UninstallDisplayName = header.UninstallDisplayName,
            CreateUninstallRegKey = header.CreateUninstallRegKey,
            Uninstallable = header.Uninstallable,
            CreatesUninstallRegistryKey = createsUninstallRegistryKey,
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
        (Architecture? headerArchitecture, bool headerConclusive) = GetHeaderArchitecture(header, stubArchitecture);
        Architecture[] payloadArchitectures = payloads
            .Select(payload => payload.Architecture)
            .Distinct()
            .ToArray();
        if (IsX86CompatibleOnly(header.ArchitecturesAllowed)
            && payloadArchitectures.Length == 1)
        {
            return (payloadArchitectures[0], true);
        }

        return (headerArchitecture, headerConclusive);
    }

    private static (Architecture? Architecture, bool Conclusive) GetHeaderArchitecture(
        InnoParsedHeader header,
        Architecture stubArchitecture)
    {
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

    private static bool IsX86CompatibleOnly(string? expression)
    {
        string value = expression ?? "";
        return HasPositiveToken(value, "x86compatible")
            && !HasPositiveToken(value, "x64compatible")
            && !HasPositiveToken(value, "x64os")
            && !HasPositiveToken(value, "arm64");
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

    private static string? NormalizeInstallLocation(InnoSetupMetadata metadata)
    {
        string? safe = SafeMetadataValue(metadata.DefaultDirName);
        if (safe is null)
        {
            return null;
        }

        string? resolved;
        if (StartsWithConstant(safe, "{autopf}", out string autoSuffix)
            || StartsWithConstant(safe, "{pf}", out autoSuffix))
        {
            resolved = metadata.Scope switch
            {
                Scope.User => "%LOCALAPPDATA%\\Programs" + autoSuffix,
                Scope.Machine when Get64BitInstallMode(metadata) == true => "%ProgramFiles%" + autoSuffix,
                Scope.Machine when Get64BitInstallMode(metadata) == false => "%ProgramFiles(x86)%" + autoSuffix,
                _ => null,
            };
        }
        else if (StartsWithConstant(safe, "{pf64}", out string pf64Suffix))
        {
            resolved = metadata.Scope == Scope.Machine && Get64BitInstallMode(metadata) == true
                ? "%ProgramFiles%" + pf64Suffix
                : null;
        }
        else if (StartsWithConstant(safe, "{pf32}", out string pf32Suffix))
        {
            resolved = metadata.Scope == Scope.Machine ? "%ProgramFiles(x86)%" + pf32Suffix : null;
        }
        else if (StartsWithConstant(safe, "{localappdata}", out string localSuffix))
        {
            resolved = metadata.Scope == Scope.User ? "%LOCALAPPDATA%" + localSuffix : null;
        }
        else if (StartsWithConstant(safe, "{userappdata}", out string roamingSuffix))
        {
            resolved = metadata.Scope == Scope.User ? "%APPDATA%" + roamingSuffix : null;
        }
        else
        {
            resolved = safe.Contains('{', StringComparison.Ordinal) ? null : safe;
        }

        return resolved is not null && !resolved.Contains('{', StringComparison.Ordinal) ? resolved : null;
    }

    private static bool? Get64BitInstallMode(InnoSetupMetadata metadata)
    {
        string expression = metadata.ArchitecturesInstallIn64BitMode ?? "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        return metadata.EffectiveArchitecture switch
        {
            Architecture.X64 when HasPositiveToken(expression, "x64compatible")
                || HasPositiveToken(expression, "x64os") => true,
            Architecture.Arm64 when HasPositiveToken(expression, "arm64") => true,
            Architecture.X86 when !HasPositiveToken(expression, "x64compatible")
                && !HasPositiveToken(expression, "x64os")
                && !HasPositiveToken(expression, "arm64") => false,
            _ => null,
        };
    }

    private static bool StartsWithConstant(string value, string constant, out string suffix)
    {
        if (value.StartsWith(constant, StringComparison.OrdinalIgnoreCase))
        {
            suffix = value[constant.Length..];
            return true;
        }

        suffix = "";
        return false;
    }

    private static bool? GetCreatesUninstallRegistryKey(string? createKey, string? uninstallable)
    {
        bool? create = ParseConstantBoolean(createKey);
        bool? canUninstall = ParseConstantBoolean(uninstallable);
        if (create == false || canUninstall == false)
        {
            return false;
        }

        return create == true && canUninstall == true ? true : null;
    }

    private static bool? ParseConstantBoolean(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null,
        };

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
