namespace WinMatsch.Analysis.Inno;

/// <summary>Resource limits used while reading untrusted Inno Setup data.</summary>
public sealed class InnoProbeOptions
{
    public int MaximumLoaderScanBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumStoredHeaderBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumExpandedHeaderBytes { get; init; } = 32 * 1024 * 1024;

    public int MaximumLzmaDictionaryBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumStringBytes { get; init; } = 1024 * 1024;

    public int MaximumTotalStringBytes { get; init; } = 8 * 1024 * 1024;

    public int MaximumCompiledCodeBytes { get; init; } = 8 * 1024 * 1024;

    public int MaximumLanguages { get; init; } = 256;

    public int MaximumPayloadScanBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumExpandedPayloadBytes { get; init; } = 128 * 1024 * 1024;

    public int MaximumAggregatePayloadBytes { get; init; } = 192 * 1024 * 1024;

    public int MaximumPayloadMarkerAttempts { get; init; } = 64;

    public int MaximumPayloadCandidates { get; init; } = 64;

    public int MaximumArchitectureExpressionCharacters { get; init; } = 4096;

    public int MaximumArchitectureExpressionTokens { get; init; } = 256;

    public int MaximumArchitectureExpressionNesting { get; init; } = 32;

    internal void Validate()
    {
        if (MaximumLoaderScanBytes <= 0
            || MaximumStoredHeaderBytes <= 0
            || MaximumExpandedHeaderBytes <= 0
            || MaximumLzmaDictionaryBytes <= 0
            || MaximumStringBytes <= 0
            || MaximumTotalStringBytes <= 0
            || MaximumCompiledCodeBytes <= 0
            || MaximumLanguages <= 0
            || MaximumPayloadScanBytes <= 0
            || MaximumExpandedPayloadBytes <= 0
            || MaximumAggregatePayloadBytes <= 0
            || MaximumPayloadMarkerAttempts <= 0
            || MaximumPayloadCandidates <= 0
            || MaximumArchitectureExpressionCharacters <= 0
            || MaximumArchitectureExpressionTokens <= 0
            || MaximumArchitectureExpressionNesting <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InnoProbeOptions), "All Inno Setup parser limits must be positive.");
        }
    }
}
