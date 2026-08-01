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
    string? Diagnostic);

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
                null);
        }

        InstallerVersionEvidenceKind kind;
        EvidenceConfidence confidence;
        bool enabled;
        if (analysis.Format is DetectedInstallerFormat.GenericInstallerExe
            or DetectedInstallerFormat.PortableExe)
        {
            kind = InstallerVersionEvidenceKind.PeVersionInfoProductVersion;
            confidence = EvidenceConfidence.High;
            enabled = policy.AllowPeVersionInfo;
        }
        else if (analysis.Format == DetectedInstallerFormat.Zip)
        {
            kind = InstallerVersionEvidenceKind.ArchiveConsensus;
            confidence = EvidenceConfidence.Medium;
            enabled = policy.AllowArchiveConsensus
                && analysis.Zip?.NestedInstallerCandidates.Count == 1;
        }
        else
        {
            return new(false, InstallerVersionEvidenceKind.Unspecified, EvidenceConfidence.Low, null);
        }

        if (!enabled || confidence < policy.MinimumConfidence)
        {
            return new(
                false,
                kind,
                confidence,
                analysis.Format == DetectedInstallerFormat.Zip
                    ? "VERSION_BINARY_POLICY:ZIP version evidence requires exactly one analyzed nested installer and the configured confidence threshold."
                    : $"VERSION_BINARY_POLICY:Binary version evidence from {analysis.Format} is disabled or below the configured confidence threshold.");
        }

        if (!IsValidVersion(analysis.ProductVersion, out string? reason))
        {
            return new(false, kind, confidence, $"VERSION_BINARY_REJECTED:{reason}");
        }

        return new(true, kind, confidence, null);
    }

    private static bool IsValidVersion(string? value, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "No ProductVersion value was present.";
            return false;
        }

        string candidate = value.Trim();
        if (InternalMarkerRegex().IsMatch(candidate))
        {
            reason = $"ProductVersion '{candidate}' contains an internal or placeholder marker.";
            return false;
        }

        if (!PackageVersion.TryCreate(candidate, out _))
        {
            reason = $"ProductVersion '{candidate}' is not a valid package version.";
            return false;
        }

        int[] components = NumericComponentRegex()
            .Matches(candidate)
            .Select(static match => int.TryParse(match.Value, out int component) ? component : -1)
            .ToArray();
        if (components.Length > 0 && components.All(static component => component == 0))
        {
            reason = $"ProductVersion '{candidate}' is an all-zero placeholder.";
            return false;
        }

        reason = null;
        return true;
    }

    [GeneratedRegex(
        @"(?:^|[-_.\s])(?:dev|debug|internal|private|placeholder|snapshot|test)(?:[-_.\s]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalMarkerRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumericComponentRegex();
}
