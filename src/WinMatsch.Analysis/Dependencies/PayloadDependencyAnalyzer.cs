using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyzeArchive(stream);
        }

        byte[] image = ReadBounded(stream, _options.MaximumPayloadBytes, fileName);
        PePayload payload = InspectPe(Path.GetFileName(fileName), image);
        return new PayloadDependencyAnalysis(CreatePeEvidence(payload, runtimeConfig: null, nearbyHostFxr: null));
    }

    private PayloadDependencyAnalysis AnalyzeArchive(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > _options.MaximumArchiveEntries)
        {
            throw new InvalidDataException(
                $"Archive contains {archive.Entries.Count} entries, exceeding the analysis limit of {_options.MaximumArchiveEntries}.");
        }

        var pePayloads = new List<PePayload>();
        var runtimeConfigs = new List<RuntimeConfigPayload>();
        var hostFxrPayloads = new List<PePayload>();
        long totalBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
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

            if (entry.Length > _options.MaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Archive payload '{path}' is {entry.Length} bytes, exceeding the per-payload analysis limit of {_options.MaximumPayloadBytes}.");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > _options.MaximumTotalPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Relevant archive payloads exceed the total analysis limit of {_options.MaximumTotalPayloadBytes} bytes.");
            }

            using Stream entryStream = entry.Open();
            byte[] content = ReadBounded(entryStream, _options.MaximumPayloadBytes, path);
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

        var evidence = new List<DependencyEvidence>();
        var matchedConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PePayload payload in pePayloads)
        {
            RuntimeConfigPayload? runtimeConfig = FindRuntimeConfig(payload.Path, runtimeConfigs);
            if (runtimeConfig is not null)
            {
                matchedConfigs.Add(runtimeConfig.Path);
            }

            PePayload? nearbyHostFxr = FindNearbyHostFxr(payload.Path, hostFxrPayloads);
            evidence.AddRange(CreatePeEvidence(payload, runtimeConfig, nearbyHostFxr));
        }

        foreach (RuntimeConfigPayload runtimeConfig in runtimeConfigs)
        {
            if (!matchedConfigs.Contains(runtimeConfig.Path))
            {
                evidence.Add(CreateUnassociatedRuntimeConfigEvidence(runtimeConfig));
            }
        }

        return new PayloadDependencyAnalysis(evidence);
    }

    private PePayload InspectPe(string path, byte[] image)
        => new(
            path,
            PeImportReader.Inspect(
                image,
                _options.MaximumImportDescriptors,
                _options.MaximumImportNameBytes));

    private static IReadOnlyList<DependencyEvidence> CreatePeEvidence(
        PePayload payload,
        RuntimeConfigPayload? runtimeConfig,
        PePayload? nearbyHostFxr)
    {
        PeImportInspection pe = payload.Inspection;
        string[] vcImports = pe.ImportedModules
            .Where(IsVisualCppRuntimeModule)
            .Select(static module => module.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        DependencyEvidenceStatus vcStatus = vcImports.Length > 0
            ? DependencyEvidenceStatus.Detected
            : pe.IsComplete
                ? DependencyEvidenceStatus.Absent
                : DependencyEvidenceStatus.Ambiguous;

        var vcEvidence = new DependencyEvidence
        {
            PayloadPath = payload.Path,
            Architecture = pe.Architecture,
            Kind = DependencyEvidenceKind.VisualCppRuntime,
            Status = vcStatus,
            Signals = vcImports,
        };

        RuntimeConfigInspection runtime = runtimeConfig?.Inspection ?? RuntimeConfigInspection.Absent;
        var runtimeSignals = new List<string>(runtime.Signals);
        if (IsHostFxr(payload.Path))
        {
            runtimeSignals.Add($"bundled-hostfxr:{payload.Path}");
        }
        else if (nearbyHostFxr is not null)
        {
            runtimeSignals.Add($"bundled-hostfxr:{nearbyHostFxr.Path}");
        }

        DependencyEvidenceStatus dotNetStatus = runtimeConfig is not null
            ? runtime.Status
            : nearbyHostFxr is not null || IsHostFxr(payload.Path)
                ? DependencyEvidenceStatus.Ambiguous
                : pe.IsComplete
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
        string directory = GetDirectory(pePath);
        string baseName = Path.GetFileNameWithoutExtension(pePath);
        string expected = CombinePath(directory, baseName + RuntimeConfigSuffix);
        return runtimeConfigs.FirstOrDefault(
            config => string.Equals(config.Path, expected, StringComparison.OrdinalIgnoreCase));
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

    private static byte[] ReadBounded(Stream stream, long maximumBytes, string payloadPath)
    {
        if (stream.CanSeek && stream.Length - stream.Position > maximumBytes)
        {
            throw new InvalidDataException(
                $"Payload '{payloadPath}' exceeds the analysis limit of {maximumBytes} bytes.");
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Payload '{payloadPath}' exceeds the analysis limit of {maximumBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool HasExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAndValidatePath(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathFullyQualified(normalized))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' uses an absolute path.");
        }

        if (normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' contains a '..' segment.");
        }

        return normalized;
    }

    private static string GetDirectory(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private static string CombinePath(string directory, string fileName)
        => directory.Length == 0 ? fileName : $"{directory}/{fileName}";

    [GeneratedRegex(@"^(?:vcruntime|msvcp|concrt)\d+(?:_\d+)?d?\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualCppRuntimeModuleRegex();

    private sealed record PePayload(string Path, PeImportInspection Inspection);

    private sealed record RuntimeConfigPayload(string Path, RuntimeConfigInspection Inspection);

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
