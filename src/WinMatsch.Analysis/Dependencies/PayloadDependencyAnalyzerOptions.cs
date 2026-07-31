namespace WinMatsch.Analysis.Dependencies;

/// <summary>Resource limits for untrusted installer and archive inspection.</summary>
public sealed class PayloadDependencyAnalyzerOptions
{
    public const int DefaultMaximumArchiveEntries = 4096;
    public const long DefaultMaximumPayloadBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumTotalPayloadBytes = 256L * 1024 * 1024;
    public const int DefaultMaximumImportDescriptors = 1024;
    public const int DefaultMaximumImportNameBytes = 260;

    public int MaximumArchiveEntries { get; init; } = DefaultMaximumArchiveEntries;

    public long MaximumPayloadBytes { get; init; } = DefaultMaximumPayloadBytes;

    public long MaximumTotalPayloadBytes { get; init; } = DefaultMaximumTotalPayloadBytes;

    public int MaximumImportDescriptors { get; init; } = DefaultMaximumImportDescriptors;

    public int MaximumImportNameBytes { get; init; } = DefaultMaximumImportNameBytes;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTotalPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumImportDescriptors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumImportNameBytes);
    }
}
