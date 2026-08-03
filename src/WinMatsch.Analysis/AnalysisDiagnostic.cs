namespace WinMatsch.Analysis;

/// <summary>A non-fatal analyzer finding that callers should surface to the user.</summary>
public sealed record AnalysisDiagnostic(string Code, string Message, bool RequiresManualAnalysis = false);
