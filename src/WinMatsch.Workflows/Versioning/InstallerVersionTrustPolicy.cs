using System.Text.RegularExpressions;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Versioning;

public sealed record InstallerVersionTrustPolicy
{
    public bool AllowPeVersionInfo { get; init; } = true;

    public bool AllowArchiveConsensus { get; init; } = true;

    public EvidenceConfidence MinimumConfidence { get; init; } = EvidenceConfidence.Medium;
}

public sealed record InstallerVersionTrustDecision(
    bool IsTrustworthy,
    InstallerVersionEvidenceKind Kind,
    EvidenceConfidence Confidence,
    string? Diagnostic,
    bool UsesProductVersion);

public static partial class InstallerVersionTrustEvaluator
{
    public static InstallerVersionTrustDecision Evaluate(
        InstallerAnalysis analysis,
        InstallerVersionTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(policy);

        if (analysis.Format is DetectedInstallerFormat.Msi
            or DetectedInstallerFormat.Msix
            or DetectedInstallerFormat.MsixBundle)
        {
            return new(
                true,
                InstallerVersionEvidenceKind.PackageMetadata,
                EvidenceConfidence.High,
                null,
                UsesProductVersion: true);
        }

        EvidenceConfidence confidence;
        bool enabled;
        InstallerVersionEvidenceKind productKind;
        InstallerVersionEvidenceKind fileKind;
        if (analysis.Format is DetectedInstallerFormat.GenericInstallerExe
            or DetectedInstallerFormat.PortableExe)
        {
            confidence = EvidenceConfidence.High;
            enabled = policy.AllowPeVersionInfo;
            productKind = InstallerVersionEvidenceKind.PeVersionInfoProductVersion;
            fileKind = InstallerVersionEvidenceKind.PeVersionInfoFileVersion;
        }
        else if (analysis.Format == DetectedInstallerFormat.Zip)
        {
            confidence = EvidenceConfidence.Medium;
            enabled = policy.AllowArchiveConsensus
                && analysis.Zip?.NestedInstallerCandidates.Count == 1;
            productKind = InstallerVersionEvidenceKind.ArchiveConsensus;
            fileKind = InstallerVersionEvidenceKind.ArchiveFileVersionConsensus;
        }
        else
        {
            return new(
                false,
                InstallerVersionEvidenceKind.Unspecified,
                EvidenceConfidence.Low,
                null,
                UsesProductVersion: true);
        }

        if (!enabled || confidence < policy.MinimumConfidence)
        {
            return new(
                false,
                productKind,
                confidence,
                analysis.Format == DetectedInstallerFormat.Zip
                    ? "VERSION_BINARY_POLICY:ZIP version evidence requires exactly one analyzed nested installer and the configured confidence threshold."
                    : $"VERSION_BINARY_POLICY:Binary version evidence from {analysis.Format} is disabled or below the configured confidence threshold.",
                UsesProductVersion: true);
        }

        if (IsValidVersion(analysis.ProductVersion, "ProductVersion", out string? productReason))
        {
            return new(true, productKind, confidence, null, UsesProductVersion: true);
        }

        if (fileKind != InstallerVersionEvidenceKind.Unspecified
            && IsValidVersion(analysis.FileVersion, "FileVersion", out _))
        {
            return new(
                true,
                fileKind,
                confidence,
                $"VERSION_BINARY_FALLBACK:{productReason}",
                UsesProductVersion: false);
        }

        _ = IsValidVersion(analysis.FileVersion, "FileVersion", out string? rejectedFileReason);
        return new(
            false,
            fileKind == InstallerVersionEvidenceKind.Unspecified ? productKind : fileKind,
            confidence,
            $"VERSION_BINARY_REJECTED:{productReason} {rejectedFileReason}",
            UsesProductVersion: fileKind == InstallerVersionEvidenceKind.Unspecified);
    }

    public static InstallerVersionTrustDecision EvaluateFileVersion(
        InstallerAnalysis analysis,
        InstallerVersionTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(policy);

        InstallerVersionEvidenceKind kind;
        EvidenceConfidence confidence;
        bool enabled;
        if (analysis.Format is DetectedInstallerFormat.GenericInstallerExe
            or DetectedInstallerFormat.PortableExe)
        {
            kind = InstallerVersionEvidenceKind.PeVersionInfoFileVersion;
            confidence = EvidenceConfidence.High;
            enabled = policy.AllowPeVersionInfo;
        }
        else if (analysis.Format == DetectedInstallerFormat.Zip)
        {
            kind = InstallerVersionEvidenceKind.ArchiveFileVersionConsensus;
            confidence = EvidenceConfidence.Medium;
            enabled = policy.AllowArchiveConsensus
                && analysis.Zip?.NestedInstallerCandidates.Count == 1;
        }
        else
        {
            return new(
                false,
                InstallerVersionEvidenceKind.Unspecified,
                EvidenceConfidence.Low,
                null,
                UsesProductVersion: false);
        }

        if (!enabled || confidence < policy.MinimumConfidence)
        {
            return new(
                false,
                kind,
                confidence,
                analysis.Format == DetectedInstallerFormat.Zip
                    ? "VERSION_FILE_POLICY:ZIP file-version evidence requires exactly one analyzed nested installer and the configured confidence threshold."
                    : $"VERSION_FILE_POLICY:FileVersion evidence from {analysis.Format} is disabled or below the configured confidence threshold.",
                UsesProductVersion: false);
        }

        return IsValidVersion(analysis.FileVersion, "FileVersion", out string? reason)
            ? new(true, kind, confidence, null, UsesProductVersion: false)
            : new(
                false,
                kind,
                confidence,
                $"VERSION_FILE_REJECTED:{reason}",
                UsesProductVersion: false);
    }

    private static bool IsValidVersion(
        string? value,
        string field,
        out string? reason)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = $"No {field} value was present.";
            return false;
        }

        string candidate = value.Trim();
        if (InternalMarkerRegex().IsMatch(candidate))
        {
            reason = $"{field} '{candidate}' contains an internal or placeholder marker.";
            return false;
        }

        if (!VersionShapeRegex().IsMatch(candidate))
        {
            reason = $"{field} '{candidate}' mixes a version with unsupported product or build text.";
            return false;
        }

        if (!PackageVersion.TryCreate(candidate, out _))
        {
            reason = $"{field} '{candidate}' is not a valid package version.";
            return false;
        }

        int[] components = NumericComponentRegex()
            .Matches(candidate)
            .Select(static match => int.TryParse(match.Value, out int component) ? component : -1)
            .ToArray();
        if (components.Length > 0 && components.All(static component => component == 0))
        {
            reason = $"{field} '{candidate}' is an all-zero placeholder.";
            return false;
        }

        reason = null;
        return true;
    }

    [GeneratedRegex(
        @"(?:^|[-+_.\s])(?:dev|debug|internal|private|placeholder|snapshot|test)(?:[-+_.\s]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalMarkerRegex();

    [GeneratedRegex(
        @"^[vV]?\d+(?:\.\d+)+(?:[-+](?:[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionShapeRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumericComponentRegex();
}
