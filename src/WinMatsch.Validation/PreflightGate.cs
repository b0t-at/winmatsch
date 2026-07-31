using WinMatsch.Downloads;

namespace WinMatsch.Validation;

/// <summary>
/// Composes schema, package, repository, origin, and final artifact checks into the only
/// validation boundary that may precede a commit or pull request.
/// </summary>
public sealed class PreflightGate
{
    private readonly IPreflightNetwork? _network;

    public PreflightGate(IPreflightNetwork? network = null)
    {
        _network = network;
    }

    /// <summary>Runs the complete preflight without invoking a submission boundary.</summary>
    public async Task<ValidationReport> ValidateAsync(
        PreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Documents);
        ArgumentNullException.ThrowIfNull(request.Changes);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.ExistingVersions);
        ArgumentNullException.ThrowIfNull(request.InstallerArtifacts);

        var findings = new List<ValidationFinding>();
        ParsedPackage? package = ManifestPackageParser.Parse(request.Documents, findings);
        SemanticValidationResult semantic = package is null
            ? new SemanticValidationResult([], [])
            : ManifestSemanticValidator.Validate(package, request, findings);

        await ValidateUrlsAsync(semantic.Urls, request.Options.NetworkMode, findings, cancellationToken)
            .ConfigureAwait(false);

        if (!HasBlockingFindings(findings, request.Options.WarningPolicy))
        {
            await RevalidateArtifactsAsync(
                    semantic.InstallerHashes,
                    request.InstallerArtifacts,
                    request.Options.NetworkMode,
                    findings,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return CreateStableReport(findings);
    }

    /// <summary>
    /// Runs the complete preflight and invokes <paramref name="boundary"/> only when policy permits.
    /// Artifact revalidation is the final awaited operation before the boundary.
    /// </summary>
    public async Task<ValidationReport> ExecuteAsync(
        PreflightRequest request,
        IPreflightBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ValidationReport report = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!report.CanProceed(request.Options.WarningPolicy))
        {
            return report;
        }

        await boundary.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task ValidateUrlsAsync(
        IReadOnlyList<UrlTarget> urls,
        NetworkValidationMode mode,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
        {
            return;
        }

        if (mode == NetworkValidationMode.Offline)
        {
            findings.Add(new ValidationFinding(
                "VLD5001",
                ValidationSeverity.Warning,
                $"Offline mode explicitly skipped {urls.Count} installer and metadata URL probe(s)."));
            return;
        }

        if (mode == NetworkValidationMode.Skip)
        {
            findings.Add(new ValidationFinding(
                "VLD5002",
                ValidationSeverity.Info,
                $"URL probing was explicitly skipped for {urls.Count} installer and metadata URL(s)."));
            return;
        }

        if (_network is null)
        {
            findings.Add(Error(
                "VLD5003",
                "Online preflight requires an IPreflightNetwork implementation."));
            return;
        }

        foreach (UrlTarget target in urls)
        {
            try
            {
                _ = await _network.ProbeAsync(target.Url, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException exception)
            {
                findings.Add(ProbeFailure(target, exception.Message));
            }
            catch (ArgumentException exception)
            {
                findings.Add(ProbeFailure(target, exception.Message));
            }
        }
    }

    private async Task RevalidateArtifactsAsync(
        IReadOnlyList<ExpectedInstallerHash> expectedHashes,
        IReadOnlyList<InstallerArtifact> artifacts,
        NetworkValidationMode mode,
        List<ValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        if (expectedHashes.Count == 0)
        {
            return;
        }

        if (mode != NetworkValidationMode.Online)
        {
            findings.Add(Error(
                "VLD6001",
                "Submission is blocked because immediate origin SHA revalidation cannot run in offline or skipped network mode."));
            return;
        }

        if (_network is null)
        {
            findings.Add(Error(
                "VLD6002",
                "Immediate origin SHA revalidation requires an IPreflightNetwork implementation."));
            return;
        }

        var artifactsByUrl = new Dictionary<string, InstallerArtifact>(StringComparer.Ordinal);
        foreach (InstallerArtifact artifact in artifacts.OrderBy(static item => item.InstallerUrl, StringComparer.Ordinal))
        {
            if (!artifactsByUrl.TryAdd(artifact.InstallerUrl, artifact))
            {
                findings.Add(Error(
                    "VLD6003",
                    "More than one downloaded artifact was supplied for the installer URL.",
                    artifact.InstallerUrl));
            }
        }

        foreach (IGrouping<string, ExpectedInstallerHash> group in expectedHashes.GroupBy(
                     static item => item.Url,
                     StringComparer.Ordinal))
        {
            ExpectedInstallerHash[] hashes = [.. group.DistinctBy(static item => item.Sha256)];
            if (hashes.Length != 1)
            {
                findings.Add(Error(
                    "VLD6004",
                    "The same installer URL has multiple manifest SHA-256 values.",
                    group.Key));
                continue;
            }

            ExpectedInstallerHash expected = hashes[0];
            if (!artifactsByUrl.TryGetValue(expected.Url, out InstallerArtifact? artifact))
            {
                findings.Add(Error(
                    "VLD6005",
                    "No downloaded artifact is available for immediate SHA revalidation.",
                    expected.Url));
                continue;
            }

            if (!string.Equals(
                    artifact.Download.InitialUrl,
                    artifact.InstallerUrl,
                    StringComparison.Ordinal))
            {
                findings.Add(Error(
                    "VLD6006",
                    "The artifact URL does not match the download's initial URL.",
                    expected.Url));
                continue;
            }

            if (artifact.Download.Sha256 != expected.Sha256)
            {
                findings.Add(Error(
                    "VLD6007",
                    $"Manifest SHA-256 '{expected.Sha256}' does not match downloaded SHA-256 '{artifact.Download.Sha256}'.",
                    expected.Url));
                continue;
            }

            try
            {
                DownloadRevalidationResult result = await _network
                    .RevalidateAsync(artifact.Download, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status != DownloadRevalidationStatus.Unchanged)
                {
                    findings.Add(Error(
                        "VLD6008",
                        "Installer content changed during immediate pre-commit revalidation.",
                        expected.Url));
                }
                else if (result.Result.Sha256 != expected.Sha256)
                {
                    findings.Add(Error(
                        "VLD6009",
                        $"Revalidated SHA-256 '{result.Result.Sha256}' does not match manifest SHA-256 '{expected.Sha256}'.",
                        expected.Url));
                }
            }
            catch (DownloadException exception)
            {
                findings.Add(Error(
                    "VLD6010",
                    $"Immediate installer revalidation failed: {exception.Message}",
                    expected.Url));
            }
            catch (ArgumentException exception)
            {
                findings.Add(Error(
                    "VLD6010",
                    $"Immediate installer revalidation failed: {exception.Message}",
                    expected.Url));
            }
        }
    }

    private static bool HasBlockingFindings(
        IReadOnlyList<ValidationFinding> findings,
        WarningPolicy warningPolicy)
        => new ValidationReport(findings).CanProceed(warningPolicy) is false;

    private static ValidationReport CreateStableReport(IEnumerable<ValidationFinding> findings)
        => new(
            findings
                .Distinct()
                .OrderBy(static finding => finding.Code, StringComparer.Ordinal)
                .ThenBy(static finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(static finding => finding.Message, StringComparer.Ordinal));

    private static ValidationFinding ProbeFailure(UrlTarget target, string message)
        => new(
            target.Kind == UrlTargetKind.Installer ? "VLD5004" : "VLD5005",
            target.Kind == UrlTargetKind.Installer
                ? ValidationSeverity.Error
                : ValidationSeverity.Warning,
            $"{(target.Kind == UrlTargetKind.Installer ? "Installer" : "Metadata")} URL probe failed: {message}",
            target.Url);

    private static ValidationFinding Error(string code, string message, string? path = null)
        => new(code, ValidationSeverity.Error, message, path);
}
