using WinMatsch.Analysis.Pe;

namespace WinMatsch.Analysis;

/// <summary>
/// Probes an executable for one specific installer technology (Inno Setup, NSIS, Burn, ...).
/// <see cref="ExeAnalyzer"/> runs the registered probes in a fixed order and falls back to
/// generic keyword heuristics when none claims the file.
/// </summary>
public interface IExeFormatProbe
{
    /// <summary>
    /// Inspects the executable and returns its analysis, or null when the file is not this
    /// probe's format. The stream is seekable and positioned at 0; the probe must not
    /// dispose the stream or the PE file.
    /// </summary>
    public InstallerAnalysis? Probe(PeFile peFile, Stream stream);
}
