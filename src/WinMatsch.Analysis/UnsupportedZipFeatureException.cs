namespace WinMatsch.Analysis;

/// <summary>A stable domain failure for a ZIP feature the bounded analyzer cannot safely read.</summary>
public abstract class ZipAnalysisException : Exception
{
    protected ZipAnalysisException(
        string archiveName,
        string entryPath,
        AnalysisDiagnostic diagnostic,
        Exception? innerException = null)
        : base(diagnostic.Message, innerException)
    {
        ArchiveName = archiveName;
        EntryPath = entryPath;
        Diagnostic = diagnostic;
    }

    public string ArchiveName { get; }

    public string EntryPath { get; }

    public AnalysisDiagnostic Diagnostic { get; }
}

public sealed class UnsupportedZipFeatureException : ZipAnalysisException
{
    public const string DiagnosticCode = "ZIP004";

    public UnsupportedZipFeatureException(
        string archiveName,
        string entryPath,
        ushort compressionMethod,
        string compressionMethodName,
        string? unsupportedFeature = null)
        : base(
            archiveName,
            entryPath,
            new AnalysisDiagnostic(
                DiagnosticCode,
                CreateMessage(
                    archiveName,
                    entryPath,
                    compressionMethod,
                    compressionMethodName,
                    unsupportedFeature),
                RequiresManualAnalysis: true))
    {
        CompressionMethod = compressionMethod;
        CompressionMethodName = compressionMethodName;
        UnsupportedFeature = unsupportedFeature ?? "compression method";
    }

    public ushort CompressionMethod { get; }

    public string CompressionMethodName { get; }

    public string UnsupportedFeature { get; }

    private static string CreateMessage(
        string archiveName,
        string entryPath,
        ushort compressionMethod,
        string compressionMethodName,
        string? unsupportedFeature)
        => unsupportedFeature is null
            ? $"{DiagnosticCode}: Archive '{archiveName}' entry '{entryPath}' uses unsupported "
                + $"compression method {compressionMethod} ({compressionMethodName}). "
                + "Manual analysis is required."
            : $"{DiagnosticCode}: Archive '{archiveName}' entry '{entryPath}' uses compression "
                + $"method {compressionMethod} ({compressionMethodName}) with "
                + $"{unsupportedFeature}, which is not supported. Manual analysis is required.";
}

public sealed class InvalidZipEntryDataException : ZipAnalysisException
{
    public const string DiagnosticCode = "ZIP005";

    internal InvalidZipEntryDataException(
        string archiveName,
        string entryPath,
        ushort compressionMethod,
        string compressionMethodName,
        string detail,
        Exception? innerException = null)
        : base(
            archiveName,
            entryPath,
            new AnalysisDiagnostic(
                DiagnosticCode,
                $"{DiagnosticCode}: Archive '{archiveName}' entry '{entryPath}' using compression "
                    + $"method {compressionMethod} ({compressionMethodName}) could not be read safely: "
                    + $"{detail} Manual analysis is required.",
                RequiresManualAnalysis: true),
            innerException)
    {
    }
}
