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

        InnoSetupMetadata? metadata;
        try
        {
            metadata = InspectForAnalysis(peFile, stream);
        }
        catch (UnsupportedInnoVersionException exception)
        {
            VersionInfo unsupportedVersion = peFile.VersionInfo;
            return new InstallerAnalysis
            {
                Format = DetectedInstallerFormat.InnoSetup,
                Installers =
                [
                    new Installer
                    {
                        Architecture = peFile.Architecture == Architecture.X86 ? null : peFile.Architecture,
                        InstallerType = InstallerType.Inno,
                        ElevationRequirement = peFile.RequestedElevation,
                    },
                ],
                ProductName = unsupportedVersion.ProductName,
                ProductVersion = unsupportedVersion.ProductVersion,
                Publisher = unsupportedVersion.CompanyName,
                Copyright = unsupportedVersion.LegalCopyright,
                Diagnostics =
                [
                    new AnalysisDiagnostic(
                        "INNO010",
                        $"{exception.Message} Header metadata was not interpreted; verify the installer manually.",
                        RequiresManualAnalysis: true),
                ],
            };
        }

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
        string? productCode = canClaimArp ? metadata.ProductCode : null;
        var installer = new Installer
        {
            Architecture = metadata.EffectiveArchitecture,
            InstallerType = InstallerType.Inno,
            Scope = metadata.Scope,
            ElevationRequirement = metadata.ElevationRequirement,
            ProductCode = productCode,
            InstallerLocale = metadata.Languages.Count == 1 ? metadata.Languages[0].Locale : null,
            UnsupportedOSArchitectures = metadata.UnsupportedOSArchitectures.Count == 0
                ? null
                : [.. metadata.UnsupportedOSArchitectures],
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
            Diagnostics = metadata.Diagnostics,
        };
    }

    /// <summary>Returns detailed setup directives and payload evidence, or null for a non-Inno PE.</summary>
    public InnoSetupMetadata? Inspect(PeFile peFile, Stream stream)
    {
        try
        {
            return InspectForAnalysis(peFile, stream);
        }
        catch (UnsupportedInnoVersionException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    internal InnoSetupMetadata? InspectForAnalysis(PeFile peFile, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(peFile);
        ArgumentNullException.ThrowIfNull(stream);

        (InnoParsedHeader Header, InnoLoaderOffsets Offsets)? parsed = InnoFormatReader.Read(stream, _options);
        if (parsed is null)
        {
            return null;
        }

        InnoParsedHeader header = parsed.Value.Header;
        InnoPayloadScanResult payloadScan =
            InnoFormatReader.InspectPayloads(stream, parsed.Value.Offsets, header.Compression, _options);
        List<Architecture> payloadArchitectures = payloadScan.Candidates
            .OrderByDescending(payload => payload.Size)
            .Select(payload => payload.Architecture)
            .Distinct()
            .ToList();

        ArchitectureDecision decision = GetArchitecture(
            header,
            payloadScan,
            peFile.Architecture,
            _options);
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
            InnoPrivilegeLevel.Lowest => null,
            _ => peFile.RequestedElevation,
        };
        List<AnalysisDiagnostic> diagnostics =
        [
            .. header.Diagnostics,
            .. payloadScan.Diagnostics,
        ];
        if (decision.Diagnostic is not null)
        {
            diagnostics.Add(decision.Diagnostic);
        }

        bool? createsUninstallRegistryKey = GetCreatesUninstallRegistryKey(
            header.CreateUninstallRegKey,
            header.Uninstallable);
        string? appId = SafeAppId(header.AppId);
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
            EmbeddedPayloads = payloadScan.Candidates,
            PayloadInspectionIsComplete = payloadScan.IsComplete,
            UnsupportedOSArchitectures = GetUnsupportedOSArchitectures(header, _options),
            EffectiveArchitecture = decision.Architecture,
            ArchitectureIsConclusive = decision.Conclusive,
            Diagnostics = diagnostics.Distinct().ToArray(),
        };
    }

    private static ArchitectureDecision GetArchitecture(
        InnoParsedHeader header,
        InnoPayloadScanResult payloadScan,
        Architecture stubArchitecture,
        InnoProbeOptions options)
    {
        if (!InnoArchitectureExpression.TryEvaluate(
                header.ArchitecturesAllowed,
                options,
                out InnoArchitectureExpression.Evaluation allowed))
        {
            return new ArchitectureDecision(
                stubArchitecture == Architecture.X86 ? null : stubArchitecture,
                Conclusive: false,
                new AnalysisDiagnostic(
                    "INNO001",
                    $"The Inno Setup ArchitecturesAllowed expression '{header.ArchitecturesAllowed}' could not be evaluated safely.",
                    RequiresManualAnalysis: true));
        }

        (Architecture? headerArchitecture, bool headerConclusive) =
            GetHeaderArchitecture(allowed, stubArchitecture);
        PayloadSelection payload = SelectPayloadArchitecture(payloadScan.Candidates);
        bool allowedIsEmpty = string.IsNullOrWhiteSpace(header.ArchitecturesAllowed);
        if (payload.Architecture is { } payloadArchitecture
            && TryGetArchitectureTarget(payloadArchitecture, out int payloadTarget)
            && (allowedIsEmpty || (allowed.PositiveX86CompatibleTargets & payloadTarget) != 0))
        {
            return new ArchitectureDecision(
                payloadArchitecture,
                payload.Conclusive && payloadScan.IsComplete,
                payload.Diagnostic);
        }

        if (payload.Diagnostic is not null)
        {
            return new ArchitectureDecision(headerArchitecture, headerConclusive, payload.Diagnostic);
        }

        if (allowedIsEmpty
            && TryGetSingle64BitModeArchitecture(
                header.ArchitecturesInstallIn64BitMode,
                options,
                out Architecture modeArchitecture))
        {
            return new ArchitectureDecision(
                modeArchitecture,
                Conclusive: false,
                new AnalysisDiagnostic(
                    "INNO011",
                    "ArchitecturesAllowed is empty, but ArchitecturesInstallIn64BitMode supplies a 64-bit architecture hint. Verify the inferred architecture manually.",
                    RequiresManualAnalysis: true));
        }

        if (allowedIsEmpty && headerArchitecture is null)
        {
            return new ArchitectureDecision(
                stubArchitecture == Architecture.X86 ? null : stubArchitecture,
                Conclusive: false,
                new AnalysisDiagnostic(
                    "INNO001",
                    "ArchitecturesAllowed is empty and no decisive embedded payload architecture was found.",
                    RequiresManualAnalysis: true));
        }

        if (payload.Architecture is not null
            && payload.Architecture != headerArchitecture)
        {
            return new ArchitectureDecision(
                headerArchitecture,
                headerConclusive,
                new AnalysisDiagnostic(
                    "INNO012",
                    $"The Inno Setup header implies {headerArchitecture?.ToString() ?? "an unknown architecture"}, but embedded payload evidence favors {payload.Architecture}. The header result was retained.",
                    RequiresManualAnalysis: true));
        }

        if (headerArchitecture is null
            && allowed.PositiveX86CompatibleTargets != 0)
        {
            return new ArchitectureDecision(
                null,
                Conclusive: false,
                new AnalysisDiagnostic(
                    "INNO001",
                    "The Inno Setup x86compatible expression describes compatible operating systems, not proof that the installed payload is x86.",
                    RequiresManualAnalysis: true));
        }

        return new ArchitectureDecision(headerArchitecture, headerConclusive, null);
    }

    private static (Architecture? Architecture, bool Conclusive) GetHeaderArchitecture(
        InnoArchitectureExpression.Evaluation allowed,
        Architecture stubArchitecture)
    {
        int targets = allowed.Targets;
        int inferredTargets = allowed.PositiveArchitectureHints & targets;
        bool negationBroadenedTargets = (targets & ~allowed.PositiveTargetCoverage) != 0;
        bool x86CompatibilityOnly = inferredTargets == InnoArchitectureExpression.X86
            && allowed.PositiveX86CompatibleTargets != 0;
        if (!negationBroadenedTargets
            && !x86CompatibilityOnly
            && inferredTargets is InnoArchitectureExpression.X86
                or InnoArchitectureExpression.X64
                or InnoArchitectureExpression.Arm64)
        {
            return (GetArchitectureFromTarget(inferredTargets), true);
        }

        if (targets == 0 && stubArchitecture != Architecture.X86)
        {
            return (stubArchitecture, false);
        }

        return (null, false);
    }

    private static PayloadSelection SelectPayloadArchitecture(
        IReadOnlyList<InnoPayloadCandidate> payloads)
    {
        if (payloads.Count == 0)
        {
            return default;
        }

        PayloadWeight[] weights =
        [
            .. payloads
                .GroupBy(static payload => payload.Architecture)
                .Select(static group => new PayloadWeight(
                    group.Key,
                    group.Sum(static payload => payload.Size),
                    group.Max(static payload => payload.Size)))
                .OrderByDescending(static weight => weight.TotalSize)
                .ThenByDescending(static weight => weight.LargestImage),
        ];
        if (weights.Length == 1)
        {
            return new PayloadSelection(weights[0].Architecture, Conclusive: true, Diagnostic: null);
        }

        PayloadWeight first = weights[0];
        PayloadWeight second = weights[1];
        if (first.TotalSize >= second.TotalSize * 2
            && first.LargestImage > second.LargestImage)
        {
            return new PayloadSelection(
                first.Architecture,
                Conclusive: false,
                new AnalysisDiagnostic(
                    "INNO002",
                    $"Embedded Inno Setup payloads contain mixed architectures ({string.Join(", ", weights.Select(static weight => weight.Architecture))}); {first.Architecture} has the dominant valid PE payload size and was selected provisionally.",
                    RequiresManualAnalysis: true));
        }

        return new PayloadSelection(
            Architecture: null,
            Conclusive: false,
            new AnalysisDiagnostic(
                "INNO002",
                $"Embedded Inno Setup payloads contain mixed architecture evidence ({string.Join(", ", weights.Select(static weight => weight.Architecture))}); no architecture was selected from payloads.",
                RequiresManualAnalysis: true));
    }

    private static bool TryGetSingle64BitModeArchitecture(
        string? expression,
        InnoProbeOptions options,
        out Architecture architecture)
    {
        architecture = default;
        if (string.IsNullOrWhiteSpace(expression)
            || !InnoArchitectureExpression.TryEvaluate(expression, options, out InnoArchitectureExpression.Evaluation mode))
        {
            return false;
        }

        int inferred = mode.PositiveArchitectureHints & mode.Targets;
        if (inferred is InnoArchitectureExpression.X64 or InnoArchitectureExpression.Arm64)
        {
            architecture = GetArchitectureFromTarget(inferred);
            return true;
        }

        return false;
    }

    private static Architecture[] GetUnsupportedOSArchitectures(
        InnoParsedHeader header,
        InnoProbeOptions options)
    {
        if (header.HasUnsupportedIa64
            || !InnoArchitectureExpression.TryEvaluate(
                header.ArchitecturesAllowed,
                options,
                out InnoArchitectureExpression.Evaluation allowed)
            || allowed.Targets == 0
            || allowed.NegativeTargetCoverage != 0
            || (allowed.Targets & ~allowed.PositiveTargetCoverage) != 0)
        {
            return [];
        }

        List<Architecture> unsupported = [Architecture.Arm];
        if ((allowed.Targets & InnoArchitectureExpression.X86) == 0)
        {
            unsupported.Add(Architecture.X86);
        }
        if ((allowed.Targets & InnoArchitectureExpression.X64) == 0)
        {
            unsupported.Add(Architecture.X64);
        }
        if ((allowed.Targets & InnoArchitectureExpression.Arm64) == 0)
        {
            unsupported.Add(Architecture.Arm64);
        }

        return unsupported.Order().ToArray();
    }

    private static Architecture GetArchitectureFromTarget(int target)
        => target switch
        {
            InnoArchitectureExpression.X86 => Architecture.X86,
            InnoArchitectureExpression.X64 => Architecture.X64,
            _ => Architecture.Arm64,
        };

    private static bool TryGetArchitectureTarget(Architecture architecture, out int target)
    {
        target = architecture switch
        {
            Architecture.X86 => InnoArchitectureExpression.X86,
            Architecture.X64 => InnoArchitectureExpression.X64,
            Architecture.Arm64 => InnoArchitectureExpression.Arm64,
            _ => 0,
        };
        return target != 0;
    }

    private string? NormalizeInstallLocation(InnoSetupMetadata metadata)
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
                Scope.Machine when Get64BitInstallMode(metadata, _options) == true => "%ProgramFiles%" + autoSuffix,
                Scope.Machine when Get64BitInstallMode(metadata, _options) == false => "%ProgramFiles(x86)%" + autoSuffix,
                _ => null,
            };
        }
        else if (StartsWithConstant(safe, "{pf64}", out string pf64Suffix))
        {
            resolved = metadata.Scope == Scope.Machine && Get64BitInstallMode(metadata, _options) == true
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

    private static bool? Get64BitInstallMode(InnoSetupMetadata metadata, InnoProbeOptions options)
    {
        string expression = metadata.ArchitecturesInstallIn64BitMode ?? "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        if (!InnoArchitectureExpression.TryEvaluate(
                expression,
                options,
                out InnoArchitectureExpression.Evaluation evaluation))
        {
            return null;
        }

        return metadata.EffectiveArchitecture switch
        {
            Architecture.X64 => (evaluation.Targets & InnoArchitectureExpression.X64) != 0,
            Architecture.Arm64 => (evaluation.Targets & InnoArchitectureExpression.Arm64) != 0,
            Architecture.X86 => false,
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
            || safe.Contains('{', StringComparison.Ordinal)
            || safe.Contains('}', StringComparison.Ordinal))
        {
            return null;
        }

        return safe.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : safe;
    }

    private static string? SafeAppId(string? value)
    {
        string? safe = SafeMetadataValue(value);
        if (safe is null)
        {
            return null;
        }

        if (safe.StartsWith("{{", StringComparison.Ordinal))
        {
            safe = safe[1..];
        }

        return Guid.TryParseExact(safe, "B", out _) ? safe : SafeArpValue(safe);
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

    private sealed record ArchitectureDecision(
        Architecture? Architecture,
        bool Conclusive,
        AnalysisDiagnostic? Diagnostic);

    private readonly record struct PayloadWeight(
        Architecture Architecture,
        long TotalSize,
        long LargestImage);

    private readonly record struct PayloadSelection(
        Architecture? Architecture,
        bool Conclusive,
        AnalysisDiagnostic? Diagnostic);
}
