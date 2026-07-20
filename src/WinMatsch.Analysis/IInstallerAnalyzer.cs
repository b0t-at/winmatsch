namespace WinMatsch.Analysis;

/// <summary>
/// Analyzes one family of installer files, selected by file extension. Implementations are
/// registered in <see cref="FileAnalyzer"/>; they are stateless and safe to reuse.
/// </summary>
public interface IInstallerAnalyzer
{
    /// <summary>Whether this analyzer handles the given file name. Decided by extension, case-insensitively.</summary>
    public bool CanAnalyze(string fileName);

    /// <summary>
    /// Analyzes the file content. The stream is seekable and positioned at 0; the analyzer
    /// must not dispose it.
    /// </summary>
    public InstallerAnalysis Analyze(Stream stream, string fileName);
}
