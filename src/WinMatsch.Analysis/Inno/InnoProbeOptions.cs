namespace WinMatsch.Analysis.Inno;

/// <summary>Resource limits used while reading untrusted Inno Setup data.</summary>
public sealed class InnoProbeOptions
{
    public int MaximumLoaderScanBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumStoredHeaderBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumExpandedHeaderBytes { get; init; } = 32 * 1024 * 1024;

    public int MaximumStringBytes { get; init; } = 1024 * 1024;

    public int MaximumTotalStringBytes { get; init; } = 8 * 1024 * 1024;

    public int MaximumLanguages { get; init; } = 256;

    public int MaximumPayloadScanBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumExpandedPayloadBytes { get; init; } = 128 * 1024 * 1024;

    public int MaximumPayloadCandidates { get; init; } = 64;

    internal void Validate()
    {
        if (MaximumLoaderScanBytes <= 0
            || MaximumStoredHeaderBytes <= 0
            || MaximumExpandedHeaderBytes <= 0
            || MaximumStringBytes <= 0
            || MaximumTotalStringBytes <= 0
            || MaximumLanguages <= 0
            || MaximumPayloadScanBytes <= 0
            || MaximumExpandedPayloadBytes <= 0
            || MaximumPayloadCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InnoProbeOptions), "All Inno Setup parser limits must be positive.");
        }
    }
}
