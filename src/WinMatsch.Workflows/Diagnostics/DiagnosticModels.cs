using WinMatsch.Analysis;
using WinMatsch.Analysis.Dependencies;
using WinMatsch.Core;
using WinMatsch.GitHub;
using WinMatsch.Validation;

namespace WinMatsch.Workflows.Diagnostics;

public sealed record InstallerAnalysisRequest(
    string Input,
    bool CacheEnabled,
    string? CacheDirectory);

public sealed record InstallerDiagnosticResult(
    string Input,
    string FileName,
    bool IsRemote,
    bool IsFromCache,
    string Sha256,
    long SizeInBytes,
    string Confidence,
    InstallerAnalysis Analysis,
    PayloadDependencyAnalysis Dependencies);

public sealed record ManifestValidationRequest(
    IReadOnlyList<string> Paths,
    bool Offline,
    WarningPolicy WarningPolicy,
    bool CacheEnabled,
    string? CacheDirectory,
    int ConcurrentDownloads);

public sealed record ManifestValidationResult(
    NetworkValidationMode NetworkMode,
    WarningPolicy WarningPolicy,
    IReadOnlyList<string> Files,
    ValidationReport Report);

public sealed record RepositoryManifestFile(
    string Path,
    string Content);

public sealed record PackageVersionResult(
    RepositoryCoordinates Repository,
    string Reference,
    PackageIdentifier Identifier,
    PackageVersion Version,
    bool Normalized,
    IReadOnlyList<RepositoryManifestFile> Files);

public sealed record PackageVersionsResult(
    RepositoryCoordinates Repository,
    string Reference,
    PackageIdentifier Identifier,
    int Skip,
    int Limit,
    int Total,
    IReadOnlyList<PackageVersion> Versions);

public sealed class DiagnosticNotFoundException : Exception
{
    public DiagnosticNotFoundException(string message)
        : base(message)
    {
    }
}
