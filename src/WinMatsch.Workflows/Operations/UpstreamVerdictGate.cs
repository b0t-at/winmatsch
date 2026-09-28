using System.Collections.Immutable;
using System.Text;
using WinMatsch.Core;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;

namespace WinMatsch.Workflows.Operations;

/// <summary>A blocking winget-pkgs verdict the feedback workflow recorded for a package.</summary>
public sealed record UpstreamVerdict(
    long PullRequestNumber,
    FeedbackClassification Classification,
    PackageVersion? PackageVersion,
    ImmutableArray<string> Evidence,
    DateTimeOffset RecordedAt)
{
    /// <summary>Installer traits of the manifest an installation test rejected, when recorded.</summary>
    public string? InstallerTraits { get; init; }
}

/// <summary>Supplies the blocking upstream verdicts recorded for a package.</summary>
public interface IUpstreamVerdictSource
{
    public Task<ImmutableArray<UpstreamVerdict>> GetBlockingAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken);
}

/// <summary>Reads blocking verdicts from the durable feedback state store.</summary>
public sealed class FeedbackStoreVerdictSource : IUpstreamVerdictSource
{
    private readonly IFeedbackStateStore _store;
    private readonly string _repository;

    public FeedbackStoreVerdictSource(IFeedbackStateStore store, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repository = repository;
    }

    public async Task<ImmutableArray<UpstreamVerdict>> GetBlockingAsync(
        PackageIdentifier packageIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageIdentifier);
        ImmutableArray<FeedbackWorkItem> items = await _store.GetByPackageAsync(
            _repository,
            packageIdentifier,
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. items
                .Where(static item =>
                    item.State == FeedbackWorkState.Escalated
                    && UpstreamVerdictGate.IsBlocking(item.Classification))
                .Select(static item => new UpstreamVerdict(
                    item.PullRequestNumber,
                    item.Classification,
                    PackageVersion.TryCreate(item.PackageVersion, out PackageVersion? version) ? version : null,
                    item.Evidence is null ? [] : [.. item.Evidence],
                    item.RecordedAt)
                {
                    InstallerTraits = item.InstallerTraits,
                })
                .OrderBy(static verdict => verdict.PullRequestNumber),
        ];
    }
}

/// <summary>
/// Refuses to plan a submission that repeats what winget-pkgs already rejected: the same version
/// after any blocking verdict, any version after an untrusted-certificate verdict, a manifest that
/// still carries the URLs the validator named, or an installer whose type, scope and switches are
/// unchanged since an installation-test failure. Scanner and installer-availability verdicts only
/// block the exact rejected version, because new binaries or a re-uploaded asset may pass.
/// </summary>
public static class UpstreamVerdictGate
{
    public const string FindingCode = "WF_UPSTREAM_VERDICT";

    public static bool IsBlocking(FeedbackClassification classification)
        => classification is FeedbackClassification.ScannerBlocked
            or FeedbackClassification.UntrustedCertificate
            or FeedbackClassification.UrlValidationError
            or FeedbackClassification.InstallerUnavailable
            or FeedbackClassification.InstallationFailure;

    public static ImmutableArray<ValidationFinding> Evaluate(
        PackageIdentifier identifier,
        PackageVersion version,
        PackageManifests candidate,
        ImmutableArray<UpstreamVerdict> verdicts,
        IReadOnlyDictionary<string, PackageManifests> rejectedVersions)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rejectedVersions);
        if (verdicts.IsDefaultOrEmpty)
        {
            return [];
        }

        string path = ManifestPaths.GetVersionDirectory(identifier, version);
        HashSet<string> candidateUrls = CollectUrls(candidate);
        string candidateTraits = InstallerTraits(candidate.Installer);
        var findings = ImmutableArray.CreateBuilder<ValidationFinding>();
        foreach (UpstreamVerdict verdict in verdicts.OrderBy(static verdict => verdict.PullRequestNumber))
        {
            string? reason = Reason(verdict, version, candidateUrls, candidateTraits, rejectedVersions);
            if (reason is null)
            {
                continue;
            }

            findings.Add(new ValidationFinding(
                FindingCode,
                ValidationSeverity.Error,
                $"winget-pkgs PR #{verdict.PullRequestNumber} blocked {identifier.Value} with {verdict.Classification}: {reason} Fix the cause, then pass --ignore-upstream-verdict to resubmit.",
                path));
        }

        return findings.ToImmutable();
    }

    private static string? Reason(
        UpstreamVerdict verdict,
        PackageVersion version,
        HashSet<string> candidateUrls,
        string candidateTraits,
        IReadOnlyDictionary<string, PackageManifests> rejectedVersions)
    {
        if (verdict.PackageVersion is { } rejected
            && (rejected.Equals(version) || rejected.IsEquivalentTo(version)))
        {
            return $"version {version.Value} is the version that was rejected.";
        }

        switch (verdict.Classification)
        {
            case FeedbackClassification.UntrustedCertificate:
                return "the signing certificate is publisher-level, so a new version signed the same way fails again.";
            case FeedbackClassification.UrlValidationError:
                string[] overlap =
                [
                    .. verdict.Evidence
                        .Where(candidateUrls.Contains)
                        .Order(StringComparer.Ordinal),
                ];
                if (overlap.Length > 0)
                {
                    return $"the manifest still carries the URL(s) the validator rejected: {string.Join(", ", overlap)}.";
                }

                if (verdict.Evidence.IsEmpty
                    && verdict.PackageVersion is { } urlVersion
                    && rejectedVersions.TryGetValue(urlVersion.Value, out PackageManifests? rejectedByUrl)
                    && CollectUrls(rejectedByUrl).SetEquals(candidateUrls))
                {
                    return "the manifest carries exactly the metadata URLs that failed validation.";
                }

                return null;
            case FeedbackClassification.InstallationFailure:
                // The rejected version is rarely merged; the traits recorded from the rejected
                // pull request stand in for a manifest that cannot be loaded.
                string? rejectedTraits = verdict.PackageVersion is { } installVersion
                    && rejectedVersions.TryGetValue(installVersion.Value, out PackageManifests? rejectedByInstall)
                        ? InstallerTraits(rejectedByInstall.Installer)
                        : verdict.InstallerTraits;
                return rejectedTraits is not null
                    && string.Equals(rejectedTraits, candidateTraits, StringComparison.Ordinal)
                    ? "the installer type, scope and switches are unchanged since the rejected version."
                    : null;
            default:
                return null;
        }
    }

    private static HashSet<string> CollectUrls(PackageManifests manifests)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LocaleManifest locale in (LocaleManifest[])[manifests.DefaultLocale, .. manifests.Locales])
        {
            Add(urls, locale.PublisherUrl);
            Add(urls, locale.PublisherSupportUrl);
            Add(urls, locale.PrivacyUrl);
            Add(urls, locale.PackageUrl);
            Add(urls, locale.LicenseUrl);
            Add(urls, locale.CopyrightUrl);
            Add(urls, locale.ReleaseNotesUrl);
            Add(urls, locale.PurchaseUrl);
            if (locale.Agreements is { } agreements)
            {
                foreach (PackageAgreement agreement in agreements)
                {
                    Add(urls, agreement.AgreementUrl);
                }
            }

            if (locale.Documentations is { } documentations)
            {
                foreach (Documentation documentation in documentations)
                {
                    Add(urls, documentation.DocumentUrl);
                }
            }
        }

        return urls;

        static void Add(HashSet<string> urls, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                urls.Add(value.Trim());
            }
        }
    }

    /// <summary>A canonical, order-independent description of how the installers are installed.</summary>
    internal static string InstallerTraits(InstallerManifest manifest)
    {
        var lines = new List<string>();
        foreach (Installer installer in manifest.Installers ?? [])
        {
            InstallerSwitches? switches = installer.InstallerSwitches ?? manifest.InstallerSwitches;
            var line = new StringBuilder();
            line.Append("type=").Append(installer.InstallerType ?? manifest.InstallerType);
            line.Append(";nested=").Append(installer.NestedInstallerType ?? manifest.NestedInstallerType);
            line.Append(";scope=").Append(installer.Scope ?? manifest.Scope);
            line.Append(";elevation=").Append(installer.ElevationRequirement ?? manifest.ElevationRequirement);
            line.Append(";silent=").Append(switches?.Silent);
            line.Append(";silentWithProgress=").Append(switches?.SilentWithProgress);
            line.Append(";interactive=").Append(switches?.Interactive);
            line.Append(";custom=").Append(switches?.Custom);
            line.Append(";upgrade=").Append(switches?.Upgrade);
            line.Append(";log=").Append(switches?.Log);
            line.Append(";installLocation=").Append(switches?.InstallLocation);
            line.Append(";repair=").Append(switches?.Repair);
            lines.Add(line.ToString());
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }
}
