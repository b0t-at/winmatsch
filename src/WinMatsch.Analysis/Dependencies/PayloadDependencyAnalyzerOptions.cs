namespace WinMatsch.Analysis.Dependencies;

/// <summary>Resource limits for untrusted installer and archive inspection.</summary>
public sealed class PayloadDependencyAnalyzerOptions
{
    public const int DefaultMaximumArchiveEntries = AnalysisLimits.MaxDependencyArchiveEntries;
    public const long DefaultMaximumPayloadBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumTotalPayloadBytes = 256L * 1024 * 1024;
    public const long DefaultMaximumCompressedPayloadBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumTotalCompressedBytes = 256L * 1024 * 1024;
    public const long DefaultMaximumCentralDirectoryBytes = AnalysisLimits.MaxDependencyCentralDirectoryBytes;
    public const int DefaultMaximumArchiveReadOperations = 16_384;
    public const int DefaultMaximumRuntimeConfigBytes = 4 * 1024 * 1024;
    public const int DefaultMaximumImportDescriptors = 1024;
    public const int DefaultMaximumImportNameBytes = 260;

    public int MaximumArchiveEntries { get; init; } = DefaultMaximumArchiveEntries;

    public long MaximumPayloadBytes { get; init; } = DefaultMaximumPayloadBytes;

    public long MaximumTotalPayloadBytes { get; init; } = DefaultMaximumTotalPayloadBytes;

    public long MaximumCompressedPayloadBytes { get; init; } = DefaultMaximumCompressedPayloadBytes;

    public long MaximumTotalCompressedBytes { get; init; } = DefaultMaximumTotalCompressedBytes;

    public long MaximumCentralDirectoryBytes { get; init; } = DefaultMaximumCentralDirectoryBytes;

    /// <summary>
    /// Maximum decompressor read calls across relevant archive entries. This bounds work even
    /// when hostile central-directory lengths do not match the bytes actually produced.
    /// </summary>
    public int MaximumArchiveReadOperations { get; init; } = DefaultMaximumArchiveReadOperations;

    public int MaximumRuntimeConfigBytes { get; init; } = DefaultMaximumRuntimeConfigBytes;

    public int MaximumImportDescriptors { get; init; } = DefaultMaximumImportDescriptors;

    public int MaximumImportNameBytes { get; init; } = DefaultMaximumImportNameBytes;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTotalPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumPayloadBytes, int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCompressedPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTotalCompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCentralDirectoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveReadOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRuntimeConfigBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumImportDescriptors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumImportNameBytes);
    }
}
