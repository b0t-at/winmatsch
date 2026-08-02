using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMatsch.Analysis.Inno;
using WinMatsch.Analysis.Pe;
using WinMatsch.Analysis.Squirrel;

namespace WinMatsch.Analysis.Dependencies;

/// <summary>
/// Collects bounded, non-policy runtime evidence from a PE installer or ZIP payload. It does not
/// mutate manifests and deliberately keeps uncertain and absent evidence distinct from detections.
/// </summary>
public sealed partial class PayloadDependencyAnalyzer
{
    private const string RuntimeConfigSuffix = ".runtimeconfig.json";
    private const string HostFxrFileName = "hostfxr.dll";

    private readonly PayloadDependencyAnalyzerOptions _options;

    public PayloadDependencyAnalyzer(PayloadDependencyAnalyzerOptions? options = null)
    {
        _options = options ?? new PayloadDependencyAnalyzerOptions();
        _options.Validate();
    }

    /// <summary>Analyzes a PE installer or ZIP archive while leaving the input stream open.</summary>
    public PayloadDependencyAnalysis Analyze(Stream stream, string fileName)
        => AnalyzeCore(stream, fileName, CancellationToken.None);

    /// <summary>
    /// Analyzes a PE installer or ZIP archive while leaving the input stream open. Resource-limit
    /// exhaustion is returned as unavailable evidence; cancellation still propagates normally.
    /// </summary>
    public PayloadDependencyAnalysis AnalyzeWithCancellation(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
        => AnalyzeCore(stream, fileName, cancellationToken);

    private PayloadDependencyAnalysis AnalyzeCore(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string extension = Path.GetExtension(fileName);
            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return AnalyzeArchive(stream, fileName, cancellationToken);
            }

            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return AnalyzeExecutable(stream, fileName, cancellationToken);
            }

            string payloadPath = Path.GetFileName(fileName);
            const string signal = "analysis-unavailable:unsupported-packaging";
            return new PayloadDependencyAnalysis(
                CreateUnavailableEvidence(payloadPath, signal),
                [
                    new AnalysisDiagnostic(
                        "DEP001",
                        $"Dependency evidence for '{payloadPath}' is unavailable because its outer packaging is not a PE executable or ZIP archive."),
                ],
                isComplete: false);
        }
        catch (AnalysisResourceLimitException exception)
        {
            string payloadPath = Path.GetFileName(fileName);
            const string signal = "analysis-unavailable:resource-limit";
            return new PayloadDependencyAnalysis(
                CreateUnavailableEvidence(payloadPath, signal),
                [
                    new AnalysisDiagnostic(
                        "DEP003",
                        $"Dependency evidence for '{payloadPath}' is unavailable because a resource limit was reached: {exception.Message}"),
                ],
                isComplete: false);
        }
    }

    private PayloadDependencyAnalysis AnalyzeExecutable(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        string payloadPath = Path.GetFileName(fileName);
        if (!stream.CanSeek)
        {
            const string signal = "analysis-unavailable:non-seekable-executable";
            return new PayloadDependencyAnalysis(
                CreateUnavailableEvidence(payloadPath, signal),
                [new AnalysisDiagnostic("DEP001", $"Dependency evidence for '{payloadPath}' is unavailable because the executable stream is not seekable.")],
                isComplete: false);
        }

        PePayload payload = InspectPe(payloadPath, stream);
        InstallerAnalysis installerAnalysis = AnalyzeExecutableFormat(
            stream,
            fileName,
            cancellationToken);
        DetectedInstallerFormat format = installerAnalysis.Format;
        bool payloadIsDirect = format == DetectedInstallerFormat.PortableExe;
        string? outerSignal = payloadIsDirect
            ? null
            : $"outer-stub-only:{format}";
        var evidence = new List<DependencyEvidence>(CreatePeEvidence(
            payload,
            runtimeConfig: null,
            nearbyHostFxr: null,
            allowAbsent: payloadIsDirect,
            additionalSignal: outerSignal));
        var diagnostics = new List<AnalysisDiagnostic>(payloadIsDirect
            ? []
            :
            [
                new AnalysisDiagnostic(
                    "DEP002",
                    $"The outer PE of '{payloadPath}' is a wrapper stub ({format})."
                        + " Missing runtime imports in that stub are ambiguous; any bounded format-specific payload evidence is reported separately."),
            ]);
        bool isComplete = payloadIsDirect;
        if (format == DetectedInstallerFormat.InnoSetup)
        {
            isComplete = AddInnoPayloadEvidence(stream, evidence, diagnostics, cancellationToken);
        }
        else if (format == DetectedInstallerFormat.Squirrel)
        {
            isComplete = AddSquirrelPayloadEvidence(stream, evidence, cancellationToken);
            diagnostics.AddRange(installerAnalysis?.Diagnostics ?? []);
        }

        return new PayloadDependencyAnalysis(evidence, diagnostics, isComplete);
    }

    private static bool AddInnoPayloadEvidence(
        Stream stream,
        List<DependencyEvidence> evidence,
        List<AnalysisDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long savedPosition = stream.Position;
        try
        {
            stream.Position = 0;
            using var peFile = new PeFile(stream);
            stream.Position = 0;
            InnoSetupMetadata? metadata = new InnoProbe().InspectForAnalysis(peFile, stream);
            if (metadata is null)
            {
                return false;
            }

            int index = 0;
            foreach (InnoPayloadCandidate candidate in metadata.EmbeddedPayloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = new PePayload(
                    $"inno-payload/{++index}-{candidate.Architecture.ToString().ToLowerInvariant()}.exe",
                    candidate.ImportInspection);
                evidence.AddRange(CreatePeEvidence(
                    payload,
                    runtimeConfig: null,
                    nearbyHostFxr: null,
                    allowAbsent: true,
                    additionalSignal: "inno:embedded-pe"));
            }

            diagnostics.AddRange(metadata.Diagnostics.Where(static diagnostic =>
                diagnostic.Code is "INNO003" or "INNO007" or "INNO008" or "INNO009" or "INNO013"));
            return metadata.PayloadInspectionIsComplete;
        }
        catch (UnsupportedInnoVersionException)
        {
            // The outer analysis already carries the future-version diagnostic. Dependency
            // evidence remains explicitly ambiguous through the outer-stub evidence above.
            return false;
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private static bool AddSquirrelPayloadEvidence(
        Stream stream,
        List<DependencyEvidence> evidence,
        CancellationToken cancellationToken)
    {
        long savedPosition = stream.Position;
        try
        {
            stream.Position = 0;
            SquirrelPayloadInspection inspection = SquirrelProbe.InspectPayloadPeEvidence(stream);
            foreach (SquirrelPayloadPe candidate in inspection.Payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = new PePayload(
                    $"squirrel-nupkg/{candidate.Path}",
                    candidate.ImportInspection);
                evidence.AddRange(CreatePeEvidence(
                    payload,
                    runtimeConfig: null,
                    nearbyHostFxr: null,
                    allowAbsent: true,
                    additionalSignal: "squirrel:nupkg-pe"));
            }
            return inspection.IsComplete;
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    private PayloadDependencyAnalysis AnalyzeArchive(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            string payloadPath = Path.GetFileName(fileName);
            const string signal = "analysis-unavailable:non-seekable-archive";
            return new PayloadDependencyAnalysis(
                CreateUnavailableEvidence(payloadPath, signal),
                [new AnalysisDiagnostic("DEP001", $"Dependency evidence for '{payloadPath}' is unavailable because bounded ZIP validation requires a seekable stream.")],
                isComplete: false);
        }

        ZipArchiveBounds.Validate(
            stream,
            "The dependency-analysis archive",
            _options.MaximumArchiveEntries,
            _options.MaximumCentralDirectoryBytes);
        var archiveStream = new BudgetedArchiveStream(stream);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > _options.MaximumArchiveEntries)
        {
            throw new AnalysisResourceLimitException(
                $"Archive contains {archive.Entries.Count} entries, exceeding the analysis limit of {_options.MaximumArchiveEntries}.");
        }

        var pePayloads = new List<PePayload>();
        var runtimeConfigs = new List<RuntimeConfigPayload>();
        var unavailableRuntimeConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unavailableHostFxrDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostFxrPayloads = new List<PePayload>();
        var evidence = new List<DependencyEvidence>();
        var diagnostics = new List<AnalysisDiagnostic>();
        var budget = new ArchiveReadBudget(
            _options.MaximumTotalPayloadBytes,
            _options.MaximumArchiveReadOperations,
            _options.MaximumTotalCompressedBytes);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = NormalizeAndValidatePath(entry.FullName);
            if (path.EndsWith('/'))
            {
                continue;
            }

            bool isPe = HasExtension(path, ".exe") || HasExtension(path, ".dll");
            bool isRuntimeConfig = path.EndsWith(RuntimeConfigSuffix, StringComparison.OrdinalIgnoreCase);
            if (!isPe && !isRuntimeConfig)
            {
                continue;
            }

            long perPayloadLimit = isRuntimeConfig
                ? Math.Min(_options.MaximumPayloadBytes, _options.MaximumRuntimeConfigBytes)
                : _options.MaximumPayloadBytes;
            PayloadReadResult read;
            using (archiveStream.EnterCompressedRead(
                _options.MaximumCompressedPayloadBytes,
                budget))
            {
                try
                {
                    using Stream entryStream = entry.Open();
                    read = ReadPayload(
                        entryStream,
                        perPayloadLimit,
                        budget,
                        cancellationToken);
                }
                catch (CompressedReadBudgetExceededException exception)
                {
                    read = PayloadReadResult.Unavailable(
                        "compressed-byte-budget",
                        exception.Message);
                }
            }
            if (read.Content is null)
            {
                if (isRuntimeConfig)
                {
                    unavailableRuntimeConfigs.Add(path);
                }
                else if (string.Equals(
                    Path.GetFileName(path),
                    HostFxrFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    unavailableHostFxrDirectories.Add(GetDirectory(path));
                }
                string signal = $"analysis-unavailable:{read.Reason}";
                evidence.AddRange(CreateUnavailableEvidence(path, signal, isRuntimeConfig));
                diagnostics.Add(new AnalysisDiagnostic(
                    "DEP003",
                    $"Dependency evidence for archive payload '{path}' is unavailable: {read.Description}."));
                continue;
            }

            byte[] content = read.Content;
            if (content.LongLength != entry.Length)
            {
                throw new InvalidDataException(
                    $"Archive payload '{path}' expanded to {content.LongLength} bytes instead of its declared size of {entry.Length} bytes.");
            }

            if (isRuntimeConfig)
            {
                runtimeConfigs.Add(new RuntimeConfigPayload(path, ParseRuntimeConfig(content)));
            }
            else
            {
                PePayload payload = InspectPe(path, content);
                pePayloads.Add(payload);
                if (string.Equals(Path.GetFileName(path), HostFxrFileName, StringComparison.OrdinalIgnoreCase))
                {
                    hostFxrPayloads.Add(payload);
                }
            }
        }

        var matchedConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PePayload payload in pePayloads)
        {
            RuntimeConfigPayload? runtimeConfig = FindRuntimeConfig(payload.Path, runtimeConfigs);
            if (runtimeConfig is not null)
            {
                matchedConfigs.Add(runtimeConfig.Path);
            }

            PePayload? nearbyHostFxr = FindNearbyHostFxr(payload.Path, hostFxrPayloads);
            bool runtimeConfigUnavailable = unavailableRuntimeConfigs.Contains(
                GetExpectedRuntimeConfigPath(payload.Path));
            bool nearbyHostFxrUnavailable = unavailableHostFxrDirectories.Contains(
                GetDirectory(payload.Path));
            evidence.AddRange(CreatePeEvidence(
                payload,
                runtimeConfig,
                nearbyHostFxr,
                allowAbsent: true,
                additionalSignal: null,
                runtimeConfigUnavailable,
                nearbyHostFxrUnavailable));
        }

        foreach (RuntimeConfigPayload runtimeConfig in runtimeConfigs)
        {
            if (!matchedConfigs.Contains(runtimeConfig.Path))
            {
                evidence.Add(CreateUnassociatedRuntimeConfigEvidence(runtimeConfig));
            }
        }

        return new PayloadDependencyAnalysis(evidence, diagnostics, isComplete: true);
    }

    private PePayload InspectPe(string path, Stream stream)
        => new(
            path,
            PeImportReader.Inspect(
                stream,
                _options.MaximumImportDescriptors,
                _options.MaximumImportNameBytes));

    private PePayload InspectPe(string path, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return InspectPe(path, stream);
    }

    private static IReadOnlyList<DependencyEvidence> CreatePeEvidence(
        PePayload payload,
        RuntimeConfigPayload? runtimeConfig,
        PePayload? nearbyHostFxr,
        bool allowAbsent,
        string? additionalSignal,
        bool runtimeConfigUnavailable = false,
        bool nearbyHostFxrUnavailable = false)
    {
        PeImportInspection pe = payload.Inspection;
        List<string> vcSignals = pe.ImportedModules
            .Where(IsVisualCppRuntimeModule)
            .Select(static module => module.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (additionalSignal is not null)
        {
            vcSignals.Add(additionalSignal);
        }

        DependencyEvidenceStatus vcStatus = vcSignals.Any(IsVisualCppRuntimeModule)
            ? DependencyEvidenceStatus.Detected
            : pe.IsComplete && allowAbsent
                ? DependencyEvidenceStatus.Absent
                : DependencyEvidenceStatus.Ambiguous;

        var vcEvidence = new DependencyEvidence
        {
            PayloadPath = payload.Path,
            Architecture = pe.Architecture,
            Kind = DependencyEvidenceKind.VisualCppRuntime,
            Status = vcStatus,
            Signals = vcSignals,
        };

        RuntimeConfigInspection runtime = runtimeConfig?.Inspection ?? RuntimeConfigInspection.Absent;
        var runtimeSignals = new List<string>(runtime.Signals);
        if (additionalSignal is not null)
        {
            runtimeSignals.Add(additionalSignal);
        }
        if (pe.IsManaged)
        {
            runtimeSignals.Add("pe:managed-image");
        }
        if (runtimeConfigUnavailable)
        {
            runtimeSignals.Add("runtimeconfig:analysis-unavailable");
        }
        if (nearbyHostFxrUnavailable)
        {
            runtimeSignals.Add("hostfxr:analysis-unavailable");
        }

        if (IsHostFxr(payload.Path))
        {
            runtimeSignals.Add($"bundled-hostfxr:{payload.Path}");
        }
        else if (nearbyHostFxr is not null)
        {
            runtimeSignals.Add($"bundled-hostfxr:{nearbyHostFxr.Path}");
        }

        DependencyEvidenceStatus dotNetStatus = runtimeConfigUnavailable || nearbyHostFxrUnavailable
            ? DependencyEvidenceStatus.Unavailable
            : runtimeConfig is not null
            ? runtime.Status
            : nearbyHostFxr is not null || IsHostFxr(payload.Path)
                ? DependencyEvidenceStatus.Ambiguous
                : pe.IsManaged
                    ? DependencyEvidenceStatus.Ambiguous
                    : pe.IsComplete && allowAbsent
                    ? DependencyEvidenceStatus.Absent
                    : DependencyEvidenceStatus.Ambiguous;

        int? runtimeMajor = runtime.RuntimeMajor;
        if (runtimeMajor is null && IsHostFxr(payload.Path))
        {
            runtimeMajor = InferHostFxrMajor(payload.Path);
        }

        var dotNetEvidence = new DependencyEvidence
        {
            PayloadPath = payload.Path,
            Architecture = pe.Architecture,
            Kind = DependencyEvidenceKind.DotNetRuntime,
            Status = dotNetStatus,
            RuntimeMajor = runtimeMajor,
            Signals = runtimeSignals,
        };

        return [vcEvidence, dotNetEvidence];
    }

    private static IReadOnlyList<DependencyEvidence> CreateUnavailableEvidence(
        string path,
        string signal,
        bool runtimeConfigOnly = false)
    {
        if (runtimeConfigOnly)
        {
            return
            [
                new DependencyEvidence
                {
                    PayloadPath = path,
                    Kind = DependencyEvidenceKind.DotNetRuntime,
                    Status = DependencyEvidenceStatus.Unavailable,
                    Signals = [signal],
                },
            ];
        }

        return
        [
            new DependencyEvidence
            {
                PayloadPath = path,
                Kind = DependencyEvidenceKind.VisualCppRuntime,
                Status = DependencyEvidenceStatus.Unavailable,
                Signals = [signal],
            },
            new DependencyEvidence
            {
                PayloadPath = path,
                Kind = DependencyEvidenceKind.DotNetRuntime,
                Status = DependencyEvidenceStatus.Unavailable,
                Signals = [signal],
            },
        ];
    }

    private static DependencyEvidence CreateUnassociatedRuntimeConfigEvidence(RuntimeConfigPayload runtimeConfig)
    {
        RuntimeConfigInspection inspection = runtimeConfig.Inspection;
        return new DependencyEvidence
        {
            PayloadPath = runtimeConfig.Path,
            Kind = DependencyEvidenceKind.DotNetRuntime,
            Status = inspection.Status == DependencyEvidenceStatus.Absent
                ? DependencyEvidenceStatus.Absent
                : DependencyEvidenceStatus.Ambiguous,
            RuntimeMajor = inspection.RuntimeMajor,
            Signals = inspection.Signals,
        };
    }

    private static RuntimeConfigInspection ParseRuntimeConfig(byte[] content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("runtimeOptions", out JsonElement runtimeOptions)
                || runtimeOptions.ValueKind != JsonValueKind.Object)
            {
                return RuntimeConfigInspection.Ambiguous("runtimeconfig:missing-runtimeOptions");
            }

            var majors = new HashSet<int>();
            var signals = new List<string>();
            AddFrameworkVersion(runtimeOptions, "framework", majors, signals);
            AddFrameworkVersions(runtimeOptions, "frameworks", majors, signals);

            if (signals.Any(static signal => signal.StartsWith(
                    "runtimeconfig:invalid-framework-version:",
                    StringComparison.Ordinal)))
            {
                return new RuntimeConfigInspection(DependencyEvidenceStatus.Ambiguous, null, signals);
            }

            if (majors.Count > 1)
            {
                signals.Add("runtimeconfig:conflicting-runtime-majors");
                return new RuntimeConfigInspection(DependencyEvidenceStatus.Ambiguous, null, signals);
            }

            if (majors.Count == 1)
            {
                return new RuntimeConfigInspection(
                    DependencyEvidenceStatus.Detected,
                    majors.Single(),
                    signals);
            }

            if (runtimeOptions.TryGetProperty("tfm", out JsonElement tfmElement)
                && tfmElement.ValueKind == JsonValueKind.String
                && TryParseTfmMajor(tfmElement.GetString(), out int tfmMajor))
            {
                signals.Add($"runtimeconfig:tfm=net{tfmMajor}");
                return new RuntimeConfigInspection(
                    DependencyEvidenceStatus.Inferred,
                    tfmMajor,
                    signals);
            }

            signals.Add("runtimeconfig:no-shared-framework");
            return new RuntimeConfigInspection(DependencyEvidenceStatus.Absent, null, signals);
        }
        catch (JsonException)
        {
            return RuntimeConfigInspection.Ambiguous("runtimeconfig:malformed-json");
        }
    }

    private static void AddFrameworkVersion(
        JsonElement runtimeOptions,
        string propertyName,
        HashSet<int> majors,
        List<string> signals)
    {
        if (runtimeOptions.TryGetProperty(propertyName, out JsonElement framework)
            && framework.ValueKind == JsonValueKind.Object)
        {
            AddFramework(framework, majors, signals);
        }
    }

    private static void AddFrameworkVersions(
        JsonElement runtimeOptions,
        string propertyName,
        HashSet<int> majors,
        List<string> signals)
    {
        if (!runtimeOptions.TryGetProperty(propertyName, out JsonElement frameworks)
            || frameworks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement framework in frameworks.EnumerateArray())
        {
            if (framework.ValueKind == JsonValueKind.Object)
            {
                AddFramework(framework, majors, signals);
            }
        }
    }

    private static void AddFramework(
        JsonElement framework,
        HashSet<int> majors,
        List<string> signals)
    {
        string? name = framework.TryGetProperty("name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        string? version = framework.TryGetProperty("version", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String
            ? versionElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(version) || !TryParseMajor(version, out int major))
        {
            signals.Add($"runtimeconfig:invalid-framework-version:{name ?? "unknown"}");
            return;
        }

        majors.Add(major);
        signals.Add($"runtimeconfig:framework={name ?? "unknown"}@{version}");
    }

    private static RuntimeConfigPayload? FindRuntimeConfig(
        string pePath,
        IReadOnlyList<RuntimeConfigPayload> runtimeConfigs)
    {
        string expected = GetExpectedRuntimeConfigPath(pePath);
        return runtimeConfigs.FirstOrDefault(
            config => string.Equals(config.Path, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetExpectedRuntimeConfigPath(string pePath)
    {
        string directory = GetDirectory(pePath);
        string baseName = Path.GetFileNameWithoutExtension(pePath);
        return CombinePath(directory, baseName + RuntimeConfigSuffix);
    }

    private static PePayload? FindNearbyHostFxr(string pePath, IReadOnlyList<PePayload> hostFxrPayloads)
    {
        string directory = GetDirectory(pePath);
        return hostFxrPayloads.FirstOrDefault(
            hostFxr => string.Equals(GetDirectory(hostFxr.Path), directory, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVisualCppRuntimeModule(string module)
        => VisualCppRuntimeModuleRegex().IsMatch(module);

    private static bool IsHostFxr(string path)
        => string.Equals(Path.GetFileName(path), HostFxrFileName, StringComparison.OrdinalIgnoreCase);

    private static int? InferHostFxrMajor(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i + 2 < segments.Length; i++)
        {
            if (string.Equals(segments[i], "host", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], "fxr", StringComparison.OrdinalIgnoreCase)
                && TryParseMajor(segments[i + 2], out int major))
            {
                return major;
            }
        }

        return null;
    }

    private static bool TryParseMajor(string? value, out int major)
    {
        if (Version.TryParse(value, out Version? version) && version.Major > 0)
        {
            major = version.Major;
            return true;
        }

        return int.TryParse(value?.Split('.')[0], out major) && major > 0;
    }

    private static bool TryParseTfmMajor(string? tfm, out int major)
    {
        major = 0;
        if (tfm is null || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string version = tfm[3..].Split('-')[0];
        return version.Contains('.', StringComparison.Ordinal)
            && TryParseMajor(version, out major)
            && major >= 5;
    }

    private static PayloadReadResult ReadPayload(
        Stream stream,
        long maximumBytes,
        ArchiveReadBudget budget,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 64 * 1024));
        byte[] chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.TryConsumeOperation())
            {
                return PayloadReadResult.Unavailable(
                    "work-budget",
                    $"the archive read-work budget of {budget.MaximumOperations} operations was exhausted");
            }

            long remainingPerPayload = maximumBytes - total;
            long remainingAggregate = budget.RemainingBytes;
            if (remainingPerPayload <= 0 || remainingAggregate <= 0)
            {
                int extra = stream.ReadByte();
                if (extra < 0)
                {
                    return PayloadReadResult.Success(output.ToArray());
                }

                return remainingAggregate <= 0
                    ? PayloadReadResult.Unavailable(
                        "aggregate-byte-budget",
                        $"the aggregate read budget of {budget.MaximumBytes} bytes was exhausted")
                    : PayloadReadResult.Unavailable(
                        "payload-byte-budget",
                        $"it expands beyond the per-payload read budget of {maximumBytes} bytes");
            }

            int requested = (int)Math.Min(chunk.Length, Math.Min(remainingPerPayload, remainingAggregate));
            int read = stream.Read(chunk, 0, requested);
            if (read == 0)
            {
                return PayloadReadResult.Success(output.ToArray());
            }

            budget.ConsumeBytes(read);
            total += read;
            output.Write(chunk, 0, read);
        }
    }

    private static bool HasExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAndValidatePath(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || IsWindowsDriveRooted(normalized)
            || Path.IsPathFullyQualified(normalized))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' uses an absolute path.");
        }

        if (normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' contains a '..' segment.");
        }

        return normalized;
    }

    private static bool IsWindowsDriveRooted(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] == '/';

    private static string GetDirectory(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private static string CombinePath(string directory, string fileName)
        => directory.Length == 0 ? fileName : $"{directory}/{fileName}";

    private static InstallerAnalysis AnalyzeExecutableFormat(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        long savedPosition = stream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return new ExeAnalyzer().Analyze(stream, fileName);
        }
        finally
        {
            stream.Position = savedPosition;
        }
    }

    [GeneratedRegex(@"^(?:vcruntime|msvcp|concrt)\d+(?:_\d+)?d?\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualCppRuntimeModuleRegex();

    private sealed record PePayload(string Path, PeImportInspection Inspection);

    private sealed record RuntimeConfigPayload(string Path, RuntimeConfigInspection Inspection);

    private sealed record PayloadReadResult(byte[]? Content, string? Reason, string? Description)
    {
        public static PayloadReadResult Success(byte[] content) => new(content, null, null);

        public static PayloadReadResult Unavailable(string reason, string description)
            => new(null, reason, description);
    }

    private sealed class ArchiveReadBudget
    {
        public ArchiveReadBudget(
            long maximumBytes,
            int maximumOperations,
            long maximumCompressedBytes)
        {
            MaximumBytes = maximumBytes;
            MaximumOperations = maximumOperations;
            MaximumCompressedBytes = maximumCompressedBytes;
            RemainingBytes = maximumBytes;
            RemainingOperations = maximumOperations;
            RemainingCompressedBytes = maximumCompressedBytes;
        }

        public long MaximumBytes { get; }

        public int MaximumOperations { get; }

        public long MaximumCompressedBytes { get; }

        public long RemainingBytes { get; private set; }

        public int RemainingOperations { get; private set; }

        public long RemainingCompressedBytes { get; private set; }

        public bool TryConsumeOperation()
        {
            if (RemainingOperations == 0)
            {
                return false;
            }

            RemainingOperations--;
            return true;
        }

        public void ConsumeBytes(int bytes)
        {
            if (bytes < 0 || bytes > RemainingBytes)
            {
                throw new InvalidOperationException("The dependency-analysis read budget was exceeded.");
            }

            RemainingBytes -= bytes;
        }

        public void ConsumeCompressedBytes(int bytes)
        {
            if (bytes < 0 || bytes > RemainingCompressedBytes)
            {
                throw new InvalidOperationException("The compressed dependency-analysis read budget was exceeded.");
            }

            RemainingCompressedBytes -= bytes;
        }
    }

    private sealed class BudgetedArchiveStream(Stream inner) : Stream
    {
        private CompressedReadScope? _scope;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public IDisposable EnterCompressedRead(long maximumBytes, ArchiveReadBudget budget)
        {
            if (_scope is not null)
            {
                throw new InvalidOperationException("A compressed archive read budget is already active.");
            }

            _scope = new CompressedReadScope(maximumBytes, budget);
            return new CompressedReadLease(this);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (_scope is null)
            {
                return inner.Read(buffer);
            }

            long remaining = Math.Min(
                _scope.RemainingBytes,
                _scope.Budget.RemainingCompressedBytes);
            if (remaining <= 0)
            {
                Span<byte> sentinel = stackalloc byte[1];
                if (inner.Read(sentinel) == 0)
                {
                    return 0;
                }

                throw new CompressedReadBudgetExceededException(
                    $"the compressed archive data exceeds the configured per-payload or aggregate read budget");
            }

            int allowed = (int)Math.Min(buffer.Length, remaining);
            int read = inner.Read(buffer[..allowed]);
            _scope.RemainingBytes -= read;
            _scope.Budget.ConsumeCompressedBytes(read);
            return read;
        }

        public override int ReadByte()
        {
            Span<byte> value = stackalloc byte[1];
            return Read(value) == 0 ? -1 : value[0];
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private sealed class CompressedReadScope(
            long remainingBytes,
            ArchiveReadBudget budget)
        {
            public long RemainingBytes { get; set; } = remainingBytes;

            public ArchiveReadBudget Budget { get; } = budget;
        }

        private sealed class CompressedReadLease(BudgetedArchiveStream owner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (!_disposed)
                {
                    owner._scope = null;
                    _disposed = true;
                }
            }
        }
    }

    private sealed class CompressedReadBudgetExceededException(string message) : Exception(message);

    private sealed record RuntimeConfigInspection(
        DependencyEvidenceStatus Status,
        int? RuntimeMajor,
        IReadOnlyList<string> Signals)
    {
        public static RuntimeConfigInspection Absent { get; } =
            new(DependencyEvidenceStatus.Absent, null, []);

        public static RuntimeConfigInspection Ambiguous(string signal) =>
            new(DependencyEvidenceStatus.Ambiguous, null, [signal]);
    }
}
