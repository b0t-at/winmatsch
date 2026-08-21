namespace BinMatch.Analysis;

/// <summary>Identifies bounded analysis that stopped because a configured resource ceiling was reached.</summary>
public sealed class AnalysisResourceLimitException(string message, Exception? innerException = null)
    : IOException(message, innerException);
