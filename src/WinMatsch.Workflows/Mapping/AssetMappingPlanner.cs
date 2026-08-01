using System.Collections.Immutable;
using System.Text.RegularExpressions;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Versioning;

namespace WinMatsch.Workflows.Mapping;

/// <summary>Creates an ambiguity-safe mapping plan without mutating manifests or downloaded assets.</summary>
public static class AssetMappingPlanner
{
    public static AssetMappingPlan CreatePlan(AssetMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<AssetMappingDiagnostic>();
        var questions = new List<AssetMappingQuestion>();
        var decisions = new List<AssetMappingDecision>();

        request.OverridePacks.TryGet(request.PackageIdentifier, out OverridePack? pack);
        if (pack?.ManualOnly == true)
        {
            diagnostics.Add(new(
                "OVERRIDE_MANUAL_ONLY",
                AssetMappingDiagnosticSeverity.Error,
                "The package override pack requires manual mapping."));
            questions.Add(new(
                "OVERRIDE_MANUAL_ONLY",
                "Review and approve this package mapping interactively.",
                []));
        }

        Candidate[] candidates = request.Assets
            .OrderBy(static asset => asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(static asset => asset.AssetId)
            .Select(asset => BuildCandidate(asset, request, pack, diagnostics, questions))
            .ToArray();

        AddMissingUrlOverrides(request, candidates, diagnostics, questions);
        ApplySiblingCoverage(candidates, request.PreviousInstallers, diagnostics);

        var usedByPosition = new Dictionary<int, Candidate>();
        var positionsByCandidate = new Dictionary<Candidate, List<PreviousInstallerEntry>>();
        foreach (PreviousInstallerEntry previous in request.PreviousInstallers.OrderBy(static entry => entry.Position))
        {
            Candidate[] exact = candidates
                .Where(candidate => UriEquals(candidate.Asset.DownloadUri, previous.Url))
                .ToArray();
            Candidate[] matches = exact.Length > 0
                ? exact
                : candidates.Where(candidate => IsCompatible(previous, candidate)).ToArray();
            if (matches.Length == 0
                && candidates.Length == 1
                && request.PreviousInstallers.Length == 1)
            {
                matches = candidates;
            }

            if (matches.Length != 1)
            {
                string code = matches.Length == 0 ? "MAP_REMOVED" : "MAP_AMBIGUOUS";
                diagnostics.Add(new(
                    code,
                    matches.Length == 0
                        ? AssetMappingDiagnosticSeverity.Warning
                        : AssetMappingDiagnosticSeverity.Error,
                    matches.Length == 0
                        ? $"Previous installer {previous.Position} has no compatible release asset."
                        : $"Previous installer {previous.Position} matches multiple structurally compatible assets.",
                    previous.Url.AbsoluteUri,
                    previous.Position));
                questions.Add(new(
                    code,
                    matches.Length == 0
                        ? "Confirm removal or provide an explicit asset mapping override."
                        : "Select the release asset for this previous installer.",
                    [.. matches.Select(static candidate => candidate.Asset.DownloadUri.AbsoluteUri).Order(StringComparer.Ordinal)],
                    previous.Url.AbsoluteUri,
                    previous.Position));
                decisions.Add(new(
                    matches.Length == 0 ? AssetMappingDecisionKind.Removed : AssetMappingDecisionKind.Unresolved,
                    previous.Position,
                    null,
                    code,
                    EvidenceConfidence.Low));
                continue;
            }

            Candidate candidate = matches[0];
            if (!positionsByCandidate.TryGetValue(candidate, out List<PreviousInstallerEntry>? priorUsers))
            {
                priorUsers = [];
                positionsByCandidate.Add(candidate, priorUsers);
            }

            if (priorUsers.Count > 0
                && priorUsers.Any(other => !UriEquals(other.Url, previous.Url)))
            {
                diagnostics.Add(new(
                    "MAP_DUPLICATE_ASSET",
                    AssetMappingDiagnosticSeverity.Error,
                    "Distinct previous URLs cannot collapse onto one asset without an explicit structural override.",
                    candidate.Asset.DownloadUri.AbsoluteUri,
                    previous.Position));
                questions.Add(new(
                    "MAP_DUPLICATE_ASSET",
                    "Provide distinct compatible assets or explicitly approve a structural rewrite.",
                    [],
                    candidate.Asset.DownloadUri.AbsoluteUri,
                    previous.Position));
                decisions.Add(new(
                    AssetMappingDecisionKind.Unresolved,
                    previous.Position,
                    null,
                    "MAP_DUPLICATE_ASSET",
                    EvidenceConfidence.Low));
                continue;
            }

            priorUsers.Add(previous);
            usedByPosition[previous.Position] = candidate;
            AddMappedDecision(request, previous, candidate, decisions, diagnostics, questions);
        }

        HashSet<Candidate> usedCandidates = [.. usedByPosition.Values];
        foreach (Candidate candidate in candidates.Where(candidate => !usedCandidates.Contains(candidate)))
        {
            if (candidate.Architecture is null || candidate.Type is null)
            {
                diagnostics.Add(new(
                    "MAP_NEW_UNRESOLVED",
                    AssetMappingDiagnosticSeverity.Error,
                    "A new Windows asset has unresolved architecture or installer type.",
                    candidate.Asset.DownloadUri.AbsoluteUri));
                questions.Add(new(
                    "MAP_NEW_UNRESOLVED",
                    "Specify architecture and installer type for this new asset.",
                    [],
                    candidate.Asset.DownloadUri.AbsoluteUri));
                decisions.Add(new(
                    AssetMappingDecisionKind.Unresolved,
                    null,
                    null,
                    "MAP_NEW_UNRESOLVED",
                    EvidenceConfidence.Low));
                continue;
            }

            if (!request.Version.IsResolved)
            {
                decisions.Add(new(
                    AssetMappingDecisionKind.Unresolved,
                    null,
                    null,
                    "VERSION_BLOCKS_MAPPING",
                    EvidenceConfidence.Low));
                continue;
            }

            PlannedInstaller installer = CreateInstaller(
                candidate,
                null,
                request.Version.Version!.Value,
                preservePreviousStructure: false,
                diagnostics,
                questions);
            decisions.Add(new(
                AssetMappingDecisionKind.Proposed,
                null,
                installer,
                "New compatible Windows asset.",
                candidate.Confidence));
        }

        DiagnoseIncompatibleDuplicateUrls(decisions, diagnostics, questions);
        if (!request.Version.IsResolved)
        {
            diagnostics.Add(new(
                "VERSION_BLOCKS_MAPPING",
                AssetMappingDiagnosticSeverity.Error,
                "Package version resolution is unresolved or ambiguous."));
        }

        return new(
            request.Version,
            [
                .. decisions
                    .OrderBy(static decision => decision.PreviousPosition ?? int.MaxValue)
                    .ThenBy(static decision => decision.Installer?.Url.AbsoluteUri, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Kind),
            ],
            [
                .. diagnostics
                    .Distinct()
                    .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(static diagnostic => diagnostic.PreviousPosition)
                    .ThenBy(static diagnostic => diagnostic.AssetUrl, StringComparer.Ordinal),
            ],
            [
                .. questions
                    .Distinct()
                    .OrderBy(static question => question.Code, StringComparer.Ordinal)
                    .ThenBy(static question => question.PreviousPosition)
                    .ThenBy(static question => question.AssetUrl, StringComparer.Ordinal),
            ]);
    }

    private static Candidate BuildCandidate(
        DiscoveredAsset asset,
        AssetMappingRequest request,
        OverridePack? pack,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        UrlOverride[] urlOverrides = request.UrlOverrides
            .Where(item => UriEquals(item.Url, asset.DownloadUri))
            .ToArray();
        UrlOverride? urlOverride = urlOverrides.Length == 1 ? urlOverrides[0] : null;
        if (urlOverrides.Length > 1)
        {
            diagnostics.Add(new(
                "URL_OVERRIDE_AMBIGUOUS",
                AssetMappingDiagnosticSeverity.Error,
                "Multiple explicit URL overrides target the same asset.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "URL_OVERRIDE_AMBIGUOUS",
                "Retain exactly one explicit URL override for this asset.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        if (asset.Content is null)
        {
            diagnostics.Add(new(
                "CONTENT_IDENTITY_MISSING",
                AssetMappingDiagnosticSeverity.Error,
                "Downloaded SHA-256 and byte length are required before an asset can be applied.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "CONTENT_IDENTITY_MISSING",
                "Download or revalidate this asset before mapping it.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }
        else if (asset.DeclaredSize > 0
            && asset.Content.Identity.SizeInBytes != asset.DeclaredSize)
        {
            diagnostics.Add(new(
                "CONTENT_SIZE_CONFLICT",
                AssetMappingDiagnosticSeverity.Error,
                $"Release metadata size {asset.DeclaredSize} differs from downloaded size {asset.Content.Identity.SizeInBytes}.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "CONTENT_SIZE_CONFLICT",
                "Revalidate the release asset content identity.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        foreach (string analysisError in asset.Analysis?.Validate() ?? [])
        {
            diagnostics.Add(new(
                "ANALYSIS_EVIDENCE_INVALID",
                AssetMappingDiagnosticSeverity.Error,
                analysisError,
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "ANALYSIS_EVIDENCE_INVALID",
                "Re-analyze the asset with bounded, normalized archive evidence.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        if ((pack?.VanityUrls ?? []).Contains(asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal))
        {
            diagnostics.Add(new(
                "VANITY_URL_REVIEW_REQUIRED",
                AssetMappingDiagnosticSeverity.Error,
                "The override pack marks this as a rolling or vanity URL that requires interactive review.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "VANITY_URL_REVIEW_REQUIRED",
                "Confirm the downloaded content identity for this rolling URL.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        ForcedArchitectureOverride[] forced = (pack?.ForcedArchitectures ?? [])
            .Where(item => PatternMatches(item.AssetPattern, asset.AssetName)
                || PatternMatches(item.AssetPattern, asset.DownloadUri.AbsoluteUri))
            .ToArray();
        if (forced.Length > 1
            && forced.Select(static item => item.Architecture).Distinct().Count() > 1)
        {
            diagnostics.Add(new(
                "ARCH_OVERRIDE_CONFLICT",
                AssetMappingDiagnosticSeverity.Error,
                "Multiple forced architecture overrides conflict.",
                asset.DownloadUri.AbsoluteUri));
        }

        Architecture? explicitArchitecture = urlOverride?.Architecture ?? forced.FirstOrDefault()?.Architecture;
        ArchitectureTokenEvidence token = ArchitectureTokenClassifier.Classify(asset.AssetName);
        Architecture[] payload = asset.Analysis?.PayloadArchitectures.Distinct().Order().ToArray() ?? [];
        Architecture? payloadArchitecture = payload.Length == 1 ? payload[0] : null;
        Architecture? architecture = explicitArchitecture ?? ResolveArchitecture(token, payloadArchitecture);
        bool architectureConflict = token.IsAmbiguous
            || payload.Length > 1
            || (token.Architecture is not null
                && payloadArchitecture is not null
                && token.Architecture != payloadArchitecture);
        if (architectureConflict)
        {
            AssetMappingDiagnosticSeverity severity = explicitArchitecture is null
                ? AssetMappingDiagnosticSeverity.Error
                : AssetMappingDiagnosticSeverity.Warning;
            diagnostics.Add(new(
                "ARCH_CONFLICT",
                severity,
                explicitArchitecture is null
                    ? "Architecture evidence conflicts or is ambiguous."
                    : "Explicit architecture overrides conflicting token or payload evidence.",
                asset.DownloadUri.AbsoluteUri));
            if (explicitArchitecture is null)
            {
                architecture = null;
                questions.Add(new(
                    "ARCH_CONFLICT",
                    "Select an architecture for this asset.",
                    [
                        .. token.Candidates
                            .Concat(payload)
                            .Distinct()
                            .Order()
                            .Select(static item => item.ToString()),
                    ],
                    asset.DownloadUri.AbsoluteUri));
            }
        }

        InstallerType? type = ResolveInstallerType(asset);
        Scope? scope = urlOverride?.Scope
            ?? (asset.Analysis is { Scopes.Length: 1 } analysis ? analysis.Scopes[0] : null);
        AssetMappingOverride[] mappings = (pack?.AssetMappings ?? [])
            .Where(item => PatternMatches(item.AssetPattern, asset.AssetName)
                || PatternMatches(item.AssetPattern, asset.DownloadUri.AbsoluteUri))
            .ToArray();
        if (mappings.Length == 1)
        {
            architecture = mappings[0].Architecture ?? architecture;
            type = mappings[0].InstallerType ?? type;
            scope = mappings[0].Scope ?? scope;
        }
        else if (mappings.Length > 1)
        {
            diagnostics.Add(new(
                "MAP_OVERRIDE_AMBIGUOUS",
                AssetMappingDiagnosticSeverity.Error,
                "Multiple asset mapping overrides match this asset.",
                asset.DownloadUri.AbsoluteUri));
        }

        ValidateVersionContinuity(asset, request.Version, diagnostics);
        return new(
            asset,
            architecture,
            type,
            scope,
            urlOverride?.DisplayVersion,
            mappings.Length == 1 ? mappings[0].Entry : null,
            urlOverride is not null
                || mappings.Length == 1
                || forced.Length == 1
                || pack?.ScopeLayout is ScopeLayoutOverride.Root or ScopeLayoutOverride.PerInstaller,
            explicitArchitecture is not null || mappings.Length == 1
                ? EvidenceConfidence.Explicit
                : asset.Analysis is not null
                    ? EvidenceConfidence.High
                    : token.Architecture is not null
                        ? EvidenceConfidence.Medium
                        : EvidenceConfidence.Low);
    }

    private static Architecture? ResolveArchitecture(
        ArchitectureTokenEvidence token,
        Architecture? payloadArchitecture)
        => token.IsAmbiguous
            ? null
            : token.Architecture ?? payloadArchitecture;

    private static InstallerType? ResolveInstallerType(DiscoveredAsset asset)
    {
        InstallerType[] analyzed = asset.Analysis?.InstallerTypes.Distinct().ToArray() ?? [];
        if (analyzed.Length == 1)
        {
            return analyzed[0];
        }

        string name = asset.AssetName;
        if (ContainsBounded(name, "portable"))
        {
            return InstallerType.Portable;
        }

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".msi" => InstallerType.Msi,
            ".msix" or ".msixbundle" => InstallerType.Msix,
            ".appx" or ".appxbundle" => InstallerType.Appx,
            ".zip" => InstallerType.Zip,
            _ => null,
        };
    }

    private static void ApplySiblingCoverage(
        Candidate[] candidates,
        ImmutableArray<PreviousInstallerEntry> previous,
        List<AssetMappingDiagnostic> diagnostics)
    {
        Architecture[] previousArchitectures = previous
            .Select(static item => item.Architecture)
            .Where(static architecture => architecture != Architecture.Neutral)
            .Distinct()
            .ToArray();
        Architecture[] known = candidates
            .Select(static candidate => candidate.Architecture)
            .OfType<Architecture>()
            .Distinct()
            .ToArray();
        Candidate[] unresolved = candidates.Where(static candidate => candidate.Architecture is null).ToArray();
        Architecture[] missing = previousArchitectures.Except(known).ToArray();
        if (unresolved.Length == 1 && missing.Length == 1)
        {
            unresolved[0].Architecture = missing[0];
            unresolved[0].Confidence = EvidenceConfidence.Low;
            diagnostics.Add(new(
                "ARCH_SIBLING_COVERAGE",
                AssetMappingDiagnosticSeverity.Warning,
                $"Architecture {missing[0]} was inferred only from complete sibling-set coverage.",
                unresolved[0].Asset.DownloadUri.AbsoluteUri));
        }
    }

    private static bool IsCompatible(PreviousInstallerEntry previous, Candidate candidate)
        => candidate.Architecture == previous.Architecture
            && TypesCompatible(previous.InstallerType, candidate.Type)
            && (candidate.Scope is null || previous.Scope is null || candidate.Scope == previous.Scope)
            && EntryMatches(previous, candidate.Entry);

    private static bool EntryMatches(PreviousInstallerEntry previous, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(previous.Entry))
        {
            return string.Equals(entry, previous.Entry, StringComparison.OrdinalIgnoreCase);
        }

        string[] tokens = entry.Split(['-', '_', '|', '/', ':'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token =>
            string.Equals(token, previous.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, previous.Architecture.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, previous.InstallerType?.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, previous.Scope?.ToString(), StringComparison.OrdinalIgnoreCase)
            || (string.Equals(token, "setup", StringComparison.OrdinalIgnoreCase)
                && previous.InstallerType != InstallerType.Portable)
            || string.Equals(token, "entry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "installer", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TypesCompatible(InstallerType? previous, InstallerType? candidate)
        => previous is null
            || candidate is null
            || previous == candidate
            || (previous == InstallerType.Exe
                && candidate is InstallerType.Inno or InstallerType.Nullsoft or InstallerType.Wix or InstallerType.Burn);

    private static void AddMappedDecision(
        AssetMappingRequest request,
        PreviousInstallerEntry previous,
        Candidate candidate,
        List<AssetMappingDecision> decisions,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        bool exactUrl = UriEquals(previous.Url, candidate.Asset.DownloadUri);
        bool preserveIntentionalLayout = request.PreviousInstallers.Count(
            entry => UriEquals(entry.Url, previous.Url)) > 1;
        bool packagingChanged = !string.Equals(
                Path.GetExtension(previous.Url.AbsolutePath),
                Path.GetExtension(candidate.Asset.DownloadUri.AbsolutePath),
                StringComparison.OrdinalIgnoreCase)
            || (previous.InstallerType is not null
                && candidate.Type is not null
                && previous.InstallerType != candidate.Type);
        if (packagingChanged)
        {
            if (candidate.Asset.Analysis is null)
            {
                diagnostics.Add(new(
                    "MAP_PACKAGING_REANALYSIS_REQUIRED",
                    AssetMappingDiagnosticSeverity.Error,
                    "Packaging or installer type changed and fresh content analysis is required.",
                    candidate.Asset.DownloadUri.AbsoluteUri,
                    previous.Position));
                questions.Add(new(
                    "MAP_PACKAGING_REANALYSIS_REQUIRED",
                    "Analyze the changed payload before applying this mapping.",
                    [],
                    candidate.Asset.DownloadUri.AbsoluteUri,
                    previous.Position));
            }
            else
            {
                diagnostics.Add(new(
                    "MAP_PACKAGING_REANALYZED",
                    AssetMappingDiagnosticSeverity.Information,
                    "Packaging or installer type changed and was re-derived from fresh analysis.",
                    candidate.Asset.DownloadUri.AbsoluteUri,
                    previous.Position));
            }
        }

        if (exactUrl
            && previous.InstallerType is not null
            && candidate.Type is not null
            && !TypesCompatible(previous.InstallerType, candidate.Type))
        {
            diagnostics.Add(new(
                "MAP_SAME_URL_INCOMPATIBLE_TYPE",
                AssetMappingDiagnosticSeverity.Error,
                "The same URL now has an incompatible installer type.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            decisions.Add(new(
                AssetMappingDecisionKind.Unresolved,
                previous.Position,
                null,
                "MAP_SAME_URL_INCOMPATIBLE_TYPE",
                EvidenceConfidence.Low));
            return;
        }

        bool structureChanged = !preserveIntentionalLayout
            && ((candidate.Architecture is not null && previous.Architecture != candidate.Architecture)
            || (candidate.Type is not null && previous.InstallerType != candidate.Type)
            || (candidate.Scope is not null && previous.Scope != candidate.Scope));
        if (structureChanged
            && !request.AllowStructuralRewrite
            && !candidate.HasExplicitMapping)
        {
            diagnostics.Add(new(
                "MAP_STRUCTURAL_REWRITE",
                AssetMappingDiagnosticSeverity.Error,
                "An accepted architecture/type/scope layout would be rewritten without explicit approval.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            questions.Add(new(
                "MAP_STRUCTURAL_REWRITE",
                "Approve the structural rewrite or provide an explicit mapping override.",
                [],
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            decisions.Add(new(
                AssetMappingDecisionKind.Unresolved,
                previous.Position,
                null,
                "MAP_STRUCTURAL_REWRITE",
                EvidenceConfidence.Low));
            return;
        }

        PlannedInstaller installer = CreateInstaller(
            candidate,
            previous,
            request.Version.Version!.Value,
            preserveIntentionalLayout,
            diagnostics,
            questions);
        bool hashChanged = previous.Sha256 is not null
            && installer.Sha256 is not null
            && previous.Sha256 != installer.Sha256;
        if (exactUrl && hashChanged)
        {
            diagnostics.Add(new(
                "CONTENT_CHANGED_AT_STABLE_URL",
                AssetMappingDiagnosticSeverity.Warning,
                "The URL is unchanged but its downloaded SHA-256 changed.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
        }

        decisions.Add(new(
            exactUrl && !hashChanged
                ? AssetMappingDecisionKind.Preserved
                : AssetMappingDecisionKind.Updated,
            previous.Position,
            installer,
            exactUrl
                ? hashChanged
                    ? "Stable URL has new content identity."
                    : "Exact URL and accepted layout preserved."
                : "Unique structurally compatible release asset.",
            exactUrl ? EvidenceConfidence.High : candidate.Confidence));
    }

    private static PlannedInstaller CreateInstaller(
        Candidate candidate,
        PreviousInstallerEntry? previous,
        string newVersion,
        bool preservePreviousStructure,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        NestedPathResolution nested = NestedInstallerPathResolver.Resolve(previous, candidate.Asset.Analysis, newVersion);
        if (nested.ErrorCode is not null)
        {
            diagnostics.Add(new(
                nested.ErrorCode,
                AssetMappingDiagnosticSeverity.Error,
                nested.ErrorMessage!,
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous?.Position));
            questions.Add(new(
                nested.ErrorCode,
                "Select distinct nested installer paths and aliases from the bounded archive contents.",
                [
                    .. (candidate.Asset.Analysis?.NestedInstallerCandidates ?? [])
                        .Order(StringComparer.Ordinal),
                ],
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous?.Position));
        }

        return new PlannedInstaller
        {
            Url = candidate.Asset.DownloadUri,
            Sha256 = candidate.Asset.Content?.Identity.Sha256,
            Architecture = preservePreviousStructure
                ? previous!.Architecture
                : candidate.Architecture
                    ?? previous?.Architecture
                    ?? throw new InvalidOperationException("A proposed installer must have an architecture."),
            InstallerType = preservePreviousStructure
                ? previous!.InstallerType
                : candidate.Type ?? previous?.InstallerType,
            Scope = preservePreviousStructure
                ? previous!.Scope
                : candidate.Scope ?? previous?.Scope,
            DisplayVersion = candidate.DisplayVersion ?? previous?.DisplayVersion,
            NestedInstallerFiles = nested.Files,
            ArchiveBinariesDependOnPath =
                candidate.Asset.Analysis?.ArchiveBinariesDependOnPath
                ?? previous?.ArchiveBinariesDependOnPath,
        };
    }

    private static void ValidateVersionContinuity(
        DiscoveredAsset asset,
        PackageVersionResolution version,
        List<AssetMappingDiagnostic> diagnostics)
    {
        if (!version.IsResolved)
        {
            return;
        }

        string? urlVersion = PackageVersionResolver.ExtractUrlVersion(asset.DownloadUri);
        if (urlVersion is not null
            && PackageVersion.TryCreate(urlVersion, out PackageVersion? parsed)
            && !parsed!.IsEquivalentTo(version.Version!))
        {
            diagnostics.Add(new(
                "MAP_VERSION_DISCONTINUITY",
                AssetMappingDiagnosticSeverity.Error,
                $"Asset URL version '{urlVersion}' does not match package version '{version.Version}'.",
                asset.DownloadUri.AbsoluteUri));
        }
    }

    private static void AddMissingUrlOverrides(
        AssetMappingRequest request,
        Candidate[] candidates,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        foreach (UrlOverride item in request.UrlOverrides.Where(
                     item => candidates.All(candidate => !UriEquals(item.Url, candidate.Asset.DownloadUri))))
        {
            diagnostics.Add(new(
                "URL_OVERRIDE_MISSING_EVIDENCE",
                AssetMappingDiagnosticSeverity.Error,
                "An explicit URL override has no discovered content identity or analysis evidence.",
                item.Url.AbsoluteUri));
            questions.Add(new(
                "URL_OVERRIDE_MISSING_EVIDENCE",
                "Download and analyze the explicit URL before mapping it.",
                [],
                item.Url.AbsoluteUri));
        }
    }

    private static void DiagnoseIncompatibleDuplicateUrls(
        IEnumerable<AssetMappingDecision> decisions,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        foreach (IGrouping<string, PlannedInstaller> group in decisions
                     .Select(static decision => decision.Installer)
                     .OfType<PlannedInstaller>()
                     .GroupBy(static installer => installer.Url.AbsoluteUri, StringComparer.Ordinal))
        {
            if (group.Select(static installer => installer.InstallerType).Distinct().Count() > 1)
            {
                diagnostics.Add(new(
                    "MAP_DUPLICATE_URL_INCOMPATIBLE_TYPE",
                    AssetMappingDiagnosticSeverity.Error,
                    "One URL cannot represent incompatible installer types.",
                    group.Key));
                questions.Add(new(
                    "MAP_DUPLICATE_URL_INCOMPATIBLE_TYPE",
                    "Provide a distinct asset for each installer type.",
                    [],
                    group.Key));
            }
        }
    }

    private static bool PatternMatches(string pattern, string value)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsBounded(string value, string token)
    {
        int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + token.Length;
            if ((index == 0 || !char.IsAsciiLetterOrDigit(value[index - 1]))
                && (end == value.Length || !char.IsAsciiLetterOrDigit(value[end])))
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool UriEquals(Uri left, Uri right)
        => string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private sealed class Candidate(
        DiscoveredAsset asset,
        Architecture? architecture,
        InstallerType? type,
        Scope? scope,
        string? displayVersion,
        string? entry,
        bool hasExplicitMapping,
        EvidenceConfidence confidence)
    {
        public DiscoveredAsset Asset { get; } = asset;

        public Architecture? Architecture { get; set; } = architecture;

        public InstallerType? Type { get; } = type;

        public Scope? Scope { get; } = scope;

        public string? DisplayVersion { get; } = displayVersion;

        public string? Entry { get; } = entry;

        public bool HasExplicitMapping { get; } = hasExplicitMapping;

        public EvidenceConfidence Confidence { get; set; } = confidence;
    }
}
