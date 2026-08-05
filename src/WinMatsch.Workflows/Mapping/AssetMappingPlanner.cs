using System.Collections.Immutable;
using System.Text.RegularExpressions;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
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
            .SelectMany(asset => BuildCandidates(asset, request, pack, diagnostics, questions))
            .ToArray();

        AddMissingUrlOverrides(request, candidates, diagnostics, questions);
        ApplySiblingCoverage(candidates, request.PreviousInstallers, diagnostics);
        DiagnoseContentIdentityConsistency(
            candidates,
            request.AllowSharedContentAcrossUrls,
            diagnostics,
            questions);

        var usedByPosition = new Dictionary<int, Candidate>();
        var positionsByPhysicalAsset = new Dictionary<string, List<PreviousInstallerEntry>>(StringComparer.Ordinal);
        var assignedCandidates = new HashSet<Candidate>();
        Dictionary<string, Candidate[]> preservedSharedGroups = BuildPreservedSharedGroups(request, candidates);
        HashSet<int> entryTargetedRetirements = BuildEntryTargetedRetirements(request, candidates);
        foreach (PreviousInstallerEntry previous in request.PreviousInstallers.OrderBy(static entry => entry.Position))
        {
            Candidate[] availableCandidates = request.AllowStructuralRewrite
                ? [.. candidates.Where(candidate => !assignedCandidates.Contains(candidate))]
                : candidates;
            Candidate[] exactByUrl = availableCandidates
                .Where(candidate => UriEquals(candidate.Asset.DownloadUri, previous.Url))
                .ToArray();
            Candidate[] compatibleExact = exactByUrl
                .Where(candidate => IsCompatible(previous, candidate))
                .ToArray();
            bool preserveIntentionalDuplicate = request.PreviousInstallers.Count(
                    entry => UriEquals(entry.Url, previous.Url)) > 1
                && !request.AllowStructuralRewrite;
            Candidate[] exact = compatibleExact.Length > 0
                ? compatibleExact
                : exactByUrl.Length == 1
                    && ((preserveIntentionalDuplicate && exactByUrl[0].Entry is null)
                        || ((exactByUrl[0].Architecture is null
                                || exactByUrl[0].Type is null)
                            && exactByUrl[0].Entry is null))
                    ? exactByUrl
                    : exactByUrl.Length > 1
                        ? exactByUrl
                        : [];
            Candidate[] matches;
            if (preservedSharedGroups.TryGetValue(
                    previous.Url.AbsoluteUri,
                    out Candidate[]? sharedGroupCandidates))
            {
                Candidate[] compatibleShared = sharedGroupCandidates
                    .Where(candidate => IsCompatible(previous, candidate))
                    .ToArray();
                matches = compatibleShared.Length > 0
                    ? compatibleShared
                    : sharedGroupCandidates;
            }
            else
            {
                matches = exact.Length > 0
                    ? exact
                    : availableCandidates.Where(candidate => IsCompatible(previous, candidate)).ToArray();
            }

            if (matches.Length == 0
                && availableCandidates.Length == 1
                && request.PreviousInstallers.Length == 1)
            {
                matches = availableCandidates;
            }

            if (matches.Length != 1)
            {
                string code = matches.Length == 0 ? "MAP_REMOVED" : "MAP_AMBIGUOUS";
                bool retiredByEntryOverride = matches.Length == 0
                    && entryTargetedRetirements.Contains(previous.Position);
                bool approvedRemoval = matches.Length == 0
                    && (request.AllowStructuralRewrite || retiredByEntryOverride);
                diagnostics.Add(new(
                    code,
                    approvedRemoval
                        ? AssetMappingDiagnosticSeverity.Information
                        : matches.Length == 0
                        ? AssetMappingDiagnosticSeverity.Warning
                        : AssetMappingDiagnosticSeverity.Error,
                    matches.Length == 0
                        ? $"Previous installer {previous.Position} has no compatible release asset."
                        : $"Previous installer {previous.Position} matches multiple structurally compatible assets.",
                    previous.Url.AbsoluteUri,
                    previous.Position));
                if (!approvedRemoval)
                {
                    questions.Add(new(
                        code,
                        matches.Length == 0
                            ? "Confirm removal or provide an explicit asset mapping override."
                            : "Select the release asset for this previous installer.",
                        [.. matches.Select(static candidate => candidate.Asset.DownloadUri.AbsoluteUri).Order(StringComparer.Ordinal)],
                        previous.Url.AbsoluteUri,
                        previous.Position));
                }

                decisions.Add(new(
                    matches.Length == 0 ? AssetMappingDecisionKind.Removed : AssetMappingDecisionKind.Unresolved,
                    previous.Position,
                    null,
                    code,
                    EvidenceConfidence.Low));
                continue;
            }

            Candidate candidate = matches[0];
            string physicalAssetKey = GetPhysicalAssetKey(candidate.Asset);
            if (!positionsByPhysicalAsset.TryGetValue(
                    physicalAssetKey,
                    out List<PreviousInstallerEntry>? priorUsers))
            {
                priorUsers = [];
                positionsByPhysicalAsset.Add(physicalAssetKey, priorUsers);
            }

            if (priorUsers.Count > 0
                && priorUsers.Any(other => !UriEquals(other.Url, previous.Url))
                && !request.AllowStructuralRewrite)
            {
                diagnostics.Add(new(
                    "MAP_DUPLICATE_ASSET",
                    AssetMappingDiagnosticSeverity.Error,
                    "Distinct previous URLs cannot collapse onto one physical asset without explicit structural approval.",
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
            assignedCandidates.Add(candidate);
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

            if (candidate.Asset.Analysis is null)
            {
                diagnostics.Add(new(
                    "MAP_REANALYSIS_REQUIRED",
                    AssetMappingDiagnosticSeverity.Error,
                    "A new asset must be analyzed from its downloaded bytes before it can be proposed safely.",
                    candidate.Asset.DownloadUri.AbsoluteUri));
                questions.Add(new(
                    "MAP_REANALYSIS_REQUIRED",
                    "Analyze this new asset before applying its proposed entry.",
                    [],
                    candidate.Asset.DownloadUri.AbsoluteUri));
                decisions.Add(new(
                    AssetMappingDecisionKind.Unresolved,
                    null,
                    null,
                    "MAP_REANALYSIS_REQUIRED",
                    EvidenceConfidence.Low));
                continue;
            }

            PlannedInstaller installer = CreateInstaller(
                candidate,
                null,
                request.Version.Version!.Value,
                preservePreviousStructure: false,
                allowStructuralRewrite: false,
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
        DiagnoseDuplicateInstallerKeys(decisions, diagnostics, questions);
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
                    .ThenBy(static decision => decision.Installer?.Architecture)
                    .ThenBy(static decision => decision.Installer?.InstallerType)
                    .ThenBy(static decision => decision.Installer?.Scope)
                    .ThenBy(static decision => decision.Installer?.InstallerLocale?.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static decision => decision.Installer?.NestedInstallerType)
                    .ThenBy(static decision => FormatNestedInstaller(decision.Installer), StringComparer.Ordinal)
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

    private static IEnumerable<Candidate> BuildCandidates(
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
        else if (!Uri.TryCreate(asset.Content.InitialUrl, UriKind.Absolute, out Uri? initialUri)
            || !UriEquals(initialUri, asset.DownloadUri))
        {
            diagnostics.Add(new(
                "CONTENT_URL_MISMATCH",
                AssetMappingDiagnosticSeverity.Error,
                "Downloaded content evidence was requested from a different asset URL.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "CONTENT_URL_MISMATCH",
                "Download this exact release asset URL before mapping it.",
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

        ImmutableArray<string> analysisErrors = asset.Analysis?.Validate() ?? [];
        foreach (string analysisError in analysisErrors)
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

        if (!analysisErrors.IsEmpty)
        {
            yield break;
        }

        if (asset.HasOperatingSystemConflict)
        {
            diagnostics.Add(new(
                "ASSET_OS_CONFLICT",
                AssetMappingDiagnosticSeverity.Error,
                "Asset name contains conflicting Windows and non-Windows operating-system evidence.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "ASSET_OS_CONFLICT",
                "Confirm that this asset targets Windows.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        if (asset.Analysis is { } boundAnalysis
            && (asset.Content is null
                || boundAnalysis.AnalyzedContentIdentity != asset.Content.Identity
                || (!string.Equals(boundAnalysis.AnalyzedUrl, asset.Content.InitialUrl, StringComparison.Ordinal)
                    && !string.Equals(boundAnalysis.AnalyzedUrl, asset.Content.FinalUrl, StringComparison.Ordinal))))
        {
            diagnostics.Add(new(
                "ANALYSIS_IDENTITY_MISMATCH",
                AssetMappingDiagnosticSeverity.Error,
                "Analyzer evidence is not bound to this asset's downloaded URL, SHA-256, and byte length.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "ANALYSIS_IDENTITY_MISMATCH",
                "Re-analyze the exact downloaded bytes for this asset.",
                [],
                asset.DownloadUri.AbsoluteUri));
        }

        if (asset.Analysis?.Origin == AnalysisEvidenceOrigin.MetadataFixture)
        {
            diagnostics.Add(new(
                "ANALYSIS_METADATA_ONLY",
                AssetMappingDiagnosticSeverity.Error,
                "Fixture metadata can exercise mapping logic but is not installer-content validation.",
                asset.DownloadUri.AbsoluteUri));
            questions.Add(new(
                "ANALYSIS_METADATA_ONLY",
                "Run FileAnalyzer and payload analysis on the downloaded bytes before applying.",
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

        AssetMappingOverride[] mappings = (pack?.AssetMappings ?? [])
            .Where(item => PatternMatches(item.AssetPattern, asset.AssetName)
                || PatternMatches(item.AssetPattern, asset.DownloadUri.AbsoluteUri))
            .ToArray();
        if (mappings.Length > 1)
        {
            diagnostics.Add(new(
                "MAP_OVERRIDE_AMBIGUOUS",
                AssetMappingDiagnosticSeverity.Error,
                "Multiple asset mapping overrides match this asset.",
                asset.DownloadUri.AbsoluteUri));
        }

        ValidateCandidateVersion(asset, request.Version, diagnostics);
        AnalyzedInstallerShape?[] shapes = asset.Analysis is { InstallerShapes.IsEmpty: false } analysis
            ?
            [
                .. analysis.InstallerShapes
                    .OrderBy(static shape => shape.Architecture)
                    .ThenBy(static shape => shape.InstallerType)
                    .ThenBy(static shape => shape.Scope)
                    .ThenBy(static shape => shape.InstallerLocale?.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static shape => shape.NestedInstallerType)
                    .ThenBy(static shape => FormatNestedShape(shape), StringComparer.Ordinal)
                    .Cast<AnalyzedInstallerShape?>(),
            ]
            : [null];
        foreach (AnalyzedInstallerShape? shape in shapes)
        {
            yield return BuildCandidateVariant(
                asset,
                shape,
                urlOverride,
                forced,
                mappings,
                diagnostics,
                questions);
        }
    }

    private static Candidate BuildCandidateVariant(
        DiscoveredAsset asset,
        AnalyzedInstallerShape? shape,
        UrlOverride? urlOverride,
        ForcedArchitectureOverride[] forced,
        AssetMappingOverride[] mappings,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        AssetMappingOverride? mapping = mappings.Length == 1 ? mappings[0] : null;
        Architecture? explicitArchitecture =
            urlOverride?.Architecture ?? mapping?.Architecture ?? forced.FirstOrDefault()?.Architecture;
        ArchitectureTokenEvidence token = ArchitectureTokenClassifier.Classify(asset.AssetName);
        Architecture[] payload = asset.Analysis?.PayloadEvidence
            .Select(static evidence => evidence.Architecture)
            .OfType<Architecture>()
            .Distinct()
            .Order()
            .ToArray() ?? [];
        Architecture? analyzedArchitecture = shape?.Architecture
            ?? (payload.Length == 1 ? payload[0] : null);
        Architecture? architecture = explicitArchitecture
            ?? (token.IsAmbiguous ? null : token.Architecture ?? analyzedArchitecture);
        bool architectureConflict = token.IsAmbiguous
            || (shape?.Architecture is null && payload.Length > 1)
            || (token.Architecture is not null
                && analyzedArchitecture is not null
                && token.Architecture != analyzedArchitecture);
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
                            .Concat(shape?.Architecture is { } shapeArchitecture ? [shapeArchitecture] : [])
                            .Distinct()
                            .Order()
                            .Select(static item => item.ToString()),
                    ],
                    asset.DownloadUri.AbsoluteUri));
            }
        }

        InstallerType? type = mapping?.InstallerType
            ?? shape?.InstallerType
            ?? ResolveInstallerType(asset);
        Scope? scope = urlOverride?.Scope ?? mapping?.Scope ?? shape?.Scope;
        return new(
            asset,
            architecture,
            type,
            shape?.NestedInstallerType,
            scope,
            shape?.InstallerLocale,
            urlOverride?.DisplayVersion,
            mapping?.Entry,
            explicitArchitecture is not null,
            mapping?.InstallerType is not null,
            urlOverride?.Scope is not null || mapping?.Scope is not null,
            explicitArchitecture is not null || mapping is not null
                ? EvidenceConfidence.Explicit
                : asset.Analysis is not null
                    ? EvidenceConfidence.High
                    : token.Architecture is not null
                        ? EvidenceConfidence.Medium
                        : EvidenceConfidence.Low,
            shape);
    }

    private static InstallerType? ResolveInstallerType(DiscoveredAsset asset)
    {
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
            diagnostics.Add(new(
                "ARCH_SIBLING_COVERAGE",
                AssetMappingDiagnosticSeverity.Information,
                $"Sibling coverage suggests {missing[0]}, but this low-confidence signal is not applied without payload evidence or an override.",
                unresolved[0].Asset.DownloadUri.AbsoluteUri));
        }
    }

    private static bool IsCompatible(PreviousInstallerEntry previous, Candidate candidate)
        => candidate.Architecture == previous.Architecture
            && TypesCompatible(previous.InstallerType, candidate.Type)
            && (candidate.NestedType is null || candidate.NestedType == previous.NestedInstallerType)
            && (candidate.Scope is null || previous.Scope is null || candidate.Scope == previous.Scope)
            && (candidate.InstallerLocale is null
                || previous.InstallerLocale is null
                || candidate.InstallerLocale == previous.InstallerLocale)
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
            || (previous is InstallerType.Msi or InstallerType.Wix
                && candidate is InstallerType.Msi or InstallerType.Wix)
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
        if (!request.Version.IsResolved)
        {
            decisions.Add(new(
                AssetMappingDecisionKind.Unresolved,
                previous.Position,
                null,
                "VERSION_BLOCKS_MAPPING",
                EvidenceConfidence.Low));
            return;
        }

        bool exactUrl = UriEquals(previous.Url, candidate.Asset.DownloadUri);
        bool preserveIntentionalLayout = request.PreviousInstallers.Count(
                entry => UriEquals(entry.Url, previous.Url)) > 1
            && !request.AllowStructuralRewrite
            && !candidate.HasExplicitArchitecture
            && !candidate.HasExplicitType
            && !candidate.HasExplicitScope;
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

        bool previousHasNestedState = HasNestedState(previous);
        bool? analyzedArchivePathDependency =
            candidate.AnalyzedShape?.ArchiveBinariesDependOnPath
            ?? candidate.Asset.Analysis?.ArchiveBinariesDependOnPath;
        bool structureChanged = (!preserveIntentionalLayout
            && ((candidate.Architecture is not null && previous.Architecture != candidate.Architecture)
            || (candidate.Type is not null && previous.InstallerType != candidate.Type)
            || (candidate.NestedType is not null && previous.NestedInstallerType != candidate.NestedType)
            || (candidate.Scope is not null && previous.Scope != candidate.Scope)
            || (candidate.InstallerLocale is not null && previous.InstallerLocale != candidate.InstallerLocale)
            || (analyzedArchivePathDependency is not null
                && previous.ArchiveBinariesDependOnPath is not null
                && analyzedArchivePathDependency != previous.ArchiveBinariesDependOnPath)))
            || (candidate.ClearsNestedState && previousHasNestedState);
        bool unauthorizedArchitectureChange = candidate.Architecture is not null
            && previous.Architecture != candidate.Architecture
            && !candidate.HasExplicitArchitecture;
        bool unauthorizedTypeChange = candidate.Type is not null
            && previous.InstallerType != candidate.Type
            && !candidate.HasExplicitType;
        bool unauthorizedNestedTypeChange = candidate.NestedType is not null
            && previous.NestedInstallerType != candidate.NestedType;
        bool unauthorizedScopeChange = candidate.Scope is not null
            && previous.Scope is not null
            && previous.Scope != candidate.Scope
            && !candidate.HasExplicitScope;
        bool unauthorizedLocaleChange = candidate.InstallerLocale is not null
            && previous.InstallerLocale is not null
            && previous.InstallerLocale != candidate.InstallerLocale;
        bool unauthorizedArchivePathDependencyChange = analyzedArchivePathDependency is not null
            && previous.ArchiveBinariesDependOnPath is not null
            && analyzedArchivePathDependency != previous.ArchiveBinariesDependOnPath;
        bool unauthorizedNestedClear = candidate.ClearsNestedState
            && previousHasNestedState;
        if (structureChanged
            && !request.AllowStructuralRewrite
            && (unauthorizedArchitectureChange
                || unauthorizedTypeChange
                || unauthorizedNestedTypeChange
                || unauthorizedScopeChange
                || unauthorizedLocaleChange
                || unauthorizedArchivePathDependencyChange
                || unauthorizedNestedClear))
        {
            diagnostics.Add(new(
                "MAP_STRUCTURAL_REWRITE",
                AssetMappingDiagnosticSeverity.Error,
                "An accepted architecture/type/scope/nested-installer layout would be rewritten without explicit approval.",
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

        bool hashChanged = previous.Sha256 is not null
            && candidate.Asset.Content is not null
            && previous.Sha256 != candidate.Asset.Content.Identity.Sha256;
        bool artifactChanged = !exactUrl || hashChanged;
        if (artifactChanged && candidate.Asset.Analysis is null)
        {
            diagnostics.Add(new(
                "MAP_REANALYSIS_REQUIRED",
                AssetMappingDiagnosticSeverity.Error,
                "A changed asset must be analyzed from its current downloaded bytes before mapping.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            questions.Add(new(
                "MAP_REANALYSIS_REQUIRED",
                "Analyze the changed asset content before applying this mapping.",
                [],
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            decisions.Add(new(
                AssetMappingDecisionKind.Unresolved,
                previous.Position,
                null,
                "MAP_REANALYSIS_REQUIRED",
                EvidenceConfidence.Low));
            return;
        }

        ValidateVersionContinuity(previous, candidate, request.Version.Version!, diagnostics);
        PlannedInstaller installer = CreateInstaller(
            candidate,
            previous,
            request.Version.Version!.Value,
            preserveIntentionalLayout,
            request.AllowStructuralRewrite,
            diagnostics,
            questions);
        if (exactUrl && hashChanged && !request.AllowStableUrlContentChange)
        {
            diagnostics.Add(new(
                "CONTENT_CHANGED_AT_STABLE_URL",
                AssetMappingDiagnosticSeverity.Error,
                "The URL is unchanged but its downloaded SHA-256 changed; explicit content-change approval is required.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
            questions.Add(new(
                "CONTENT_CHANGED_AT_STABLE_URL",
                "Confirm the new content identity for this stable URL.",
                [],
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
        bool allowStructuralRewrite,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        bool clearNestedState = candidate.ClearsNestedState
            && (previous is null || !HasNestedState(previous) || allowStructuralRewrite);
        NestedPathResolution nested = clearNestedState
            ? NestedPathResolution.Empty
            : NestedInstallerPathResolver.Resolve(
                previous,
                candidate.Asset.Analysis,
                candidate.AnalyzedShape,
                newVersion);
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
            NestedInstallerType = preservePreviousStructure
                ? previous!.NestedInstallerType
                : clearNestedState
                    ? null
                    : candidate.NestedType ?? previous?.NestedInstallerType,
            Scope = preservePreviousStructure
                ? previous!.Scope
                : candidate.Scope ?? previous?.Scope,
            InstallerLocale = preservePreviousStructure
                ? previous!.InstallerLocale
                : candidate.InstallerLocale ?? previous?.InstallerLocale,
            DisplayVersion = candidate.DisplayVersion ?? previous?.DisplayVersion,
            NestedInstallerFiles = nested.Files,
            ArchiveBinariesDependOnPath =
                clearNestedState
                    ? null
                    : candidate.AnalyzedShape?.ArchiveBinariesDependOnPath
                        ?? candidate.Asset.Analysis?.ArchiveBinariesDependOnPath
                        ?? previous?.ArchiveBinariesDependOnPath,
        };
    }

    private static bool HasNestedState(PreviousInstallerEntry previous)
        => previous.NestedInstallerType is not null
            || !previous.NestedInstallerFiles.IsEmpty
            || previous.ArchiveBinariesDependOnPath is not null;

    private static void ValidateVersionContinuity(
        PreviousInstallerEntry previous,
        Candidate candidate,
        PackageVersion targetVersion,
        List<AssetMappingDiagnostic> diagnostics)
    {
        if (!ContainsVersionToken(previous.Url, previous.PackageVersion.Value))
        {
            return;
        }

        if (!ContainsVersionToken(candidate.Asset.DownloadUri, targetVersion.Value))
        {
            diagnostics.Add(new(
                "MAP_VERSION_DISCONTINUITY",
                AssetMappingDiagnosticSeverity.Error,
                $"The previous URL embedded version '{previous.PackageVersion}', but the mapped URL does not embed target version '{targetVersion}'.",
                candidate.Asset.DownloadUri.AbsoluteUri,
                previous.Position));
        }
    }

    private static void ValidateCandidateVersion(
        DiscoveredAsset asset,
        PackageVersionResolution resolution,
        List<AssetMappingDiagnostic> diagnostics)
    {
        if (!resolution.IsResolved)
        {
            return;
        }

        UrlVersionEvidence evidence = PackageVersionResolver.AnalyzeUrlVersion(asset.DownloadUri);
        if (evidence.IsAmbiguous)
        {
            diagnostics.Add(new(
                "MAP_VERSION_AMBIGUOUS",
                AssetMappingDiagnosticSeverity.Error,
                $"Asset URL contains multiple non-equivalent version tokens: {string.Join(", ", evidence.Candidates)}.",
                asset.DownloadUri.AbsoluteUri));
            return;
        }

        if (evidence.Version is { } urlVersion
            && PackageVersion.TryCreate(urlVersion, out PackageVersion? parsed)
            && !parsed!.IsEquivalentTo(resolution.Version!))
        {
            diagnostics.Add(new(
                "MAP_VERSION_DISCONTINUITY",
                AssetMappingDiagnosticSeverity.Error,
                $"Asset URL version '{urlVersion}' does not match target package version '{resolution.Version}'.",
                asset.DownloadUri.AbsoluteUri));
        }
    }

    private static bool ContainsVersionToken(Uri uri, string version)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        string[] representations =
        [
            version,
            version.Replace('.', '_'),
            version.Replace('.', '-'),
        ];
        return representations.Any(candidate =>
        {
            int index = path.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int end = index + candidate.Length;
                if ((index == 0 || !char.IsAsciiLetterOrDigit(path[index - 1]))
                    && (end == path.Length || !char.IsAsciiLetterOrDigit(path[end])))
                {
                    return true;
                }

                index = path.IndexOf(candidate, index + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        });
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

    private static void DiagnoseContentIdentityConsistency(
        IEnumerable<Candidate> candidates,
        bool allowSharedContentAcrossUrls,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        foreach (IGrouping<string, Candidate> group in candidates.GroupBy(
                     static candidate => candidate.Asset.DownloadUri.AbsoluteUri,
                     StringComparer.Ordinal))
        {
            DownloadContentIdentity[] identities = group
                .Select(static candidate => candidate.Asset.Content?.Identity)
                .OfType<DownloadContentIdentity>()
                .Distinct()
                .ToArray();
            if (identities.Length <= 1)
            {
                continue;
            }

            diagnostics.Add(new(
                "CONTENT_IDENTITY_CONFLICT",
                AssetMappingDiagnosticSeverity.Error,
                "The same asset URL is associated with multiple SHA-256 or byte-length identities.",
                group.Key));
            questions.Add(new(
                "CONTENT_IDENTITY_CONFLICT",
                "Revalidate the duplicated URL and retain one content identity.",
                [],
                group.Key));
        }

        if (allowSharedContentAcrossUrls)
        {
            return;
        }

        foreach (IGrouping<DownloadContentIdentity, Candidate> group in candidates
                     .Where(static candidate => candidate.Asset.Content is not null)
                     .GroupBy(static candidate => candidate.Asset.Content!.Identity))
        {
            string[] urls = group
                .Select(static candidate => candidate.Asset.DownloadUri.AbsoluteUri)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (urls.Length <= 1)
            {
                continue;
            }

            diagnostics.Add(new(
                "CONTENT_SHARED_ACROSS_URLS",
                AssetMappingDiagnosticSeverity.Error,
                "Distinct asset URLs resolve to identical SHA-256 and byte-length identities.",
                urls[0]));
            questions.Add(new(
                "CONTENT_SHARED_ACROSS_URLS",
                "Confirm that these URLs intentionally mirror the same bytes.",
                [.. urls],
                urls[0]));
        }
    }

    private static void DiagnoseDuplicateInstallerKeys(
        IEnumerable<AssetMappingDecision> decisions,
        List<AssetMappingDiagnostic> diagnostics,
        List<AssetMappingQuestion> questions)
    {
        foreach (IGrouping<string, PlannedInstaller> group in decisions
                     .Select(static decision => decision.Installer)
                     .OfType<PlannedInstaller>()
                     .GroupBy(
                         static installer =>
                             $"{installer.Architecture}|{installer.InstallerType}|{installer.Scope}|{installer.InstallerLocale?.Value.ToUpperInvariant()}",
                         StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            string[] urls = group
                .Select(static installer => installer.Url.AbsoluteUri)
                .Order(StringComparer.Ordinal)
                .ToArray();
            diagnostics.Add(new(
                "MAP_DUPLICATE_INSTALLER_KEY",
                AssetMappingDiagnosticSeverity.Error,
                $"Multiple entries share effective installer key '{group.Key}'.",
                urls[0]));
            questions.Add(new(
                "MAP_DUPLICATE_INSTALLER_KEY",
                "Remove or explicitly differentiate duplicate architecture/type/scope/locale entries.",
                [.. urls],
                urls[0]));
        }
    }

    private static string GetPhysicalAssetKey(DiscoveredAsset asset)
        => $"{asset.DownloadUri.AbsoluteUri}|{asset.Content?.Identity.Sha256}|{asset.Content?.Identity.SizeInBytes}";

    private static Dictionary<string, Candidate[]> BuildPreservedSharedGroups(
        AssetMappingRequest request,
        Candidate[] candidates)
    {
        var result = new Dictionary<string, Candidate[]>(StringComparer.Ordinal);
        if (request.AllowStructuralRewrite)
        {
            return result;
        }

        foreach (IGrouping<string, PreviousInstallerEntry> group in request.PreviousInstallers
                     .GroupBy(static previous => previous.Url.AbsoluteUri, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            PreviousInstallerEntry[] previousEntries = [.. group];
            Candidate[] compatibleCandidates = candidates
                .Where(static candidate => candidate.Entry is null)
                .Where(candidate => previousEntries.Any(previous => IsCompatible(previous, candidate)))
                .ToArray();
            IGrouping<string, Candidate>[] physicalAssets = compatibleCandidates
                .GroupBy(candidate => GetPhysicalAssetKey(candidate.Asset), StringComparer.Ordinal)
                .ToArray();
            if (physicalAssets.Length == 1)
            {
                result.Add(
                    group.Key,
                    [
                        .. physicalAssets[0]
                            .OrderBy(static candidate => candidate.Architecture)
                            .ThenBy(static candidate => candidate.Type)
                            .ThenBy(static candidate => candidate.Scope)
                            .ThenBy(static candidate => candidate.Asset.DownloadUri.AbsoluteUri, StringComparer.Ordinal),
                    ]);
            }
        }

        return result;
    }

    private static HashSet<int> BuildEntryTargetedRetirements(
        AssetMappingRequest request,
        Candidate[] candidates)
    {
        var retirements = new HashSet<int>();
        IGrouping<string, PreviousInstallerEntry>[] sharedGroups = request.PreviousInstallers
                     .GroupBy(static previous => previous.Url.AbsoluteUri, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1)
                     .ToArray();
        var candidatesByAssignedGroup = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (Candidate candidate in candidates.Where(static candidate => candidate.Entry is not null))
        {
            IGrouping<string, PreviousInstallerEntry>[] matchingGroups = sharedGroups
                .Where(group => group.Any(previous => IsCompatible(previous, candidate)))
                .ToArray();
            if (matchingGroups.Length != 1)
            {
                continue;
            }

            string groupKey = matchingGroups[0].Key;
            if (!candidatesByAssignedGroup.TryGetValue(groupKey, out List<Candidate>? assignedCandidates))
            {
                assignedCandidates = [];
                candidatesByAssignedGroup.Add(groupKey, assignedCandidates);
            }

            assignedCandidates.Add(candidate);
        }

        foreach ((string groupKey, List<Candidate> targetedCandidates) in candidatesByAssignedGroup)
        {
            PreviousInstallerEntry[] previousEntries = [.. sharedGroups.Single(group => group.Key == groupKey)];
            foreach (PreviousInstallerEntry previous in previousEntries)
            {
                if (targetedCandidates.All(candidate => !IsCompatible(previous, candidate)))
                {
                    retirements.Add(previous.Position);
                }
            }
        }

        return retirements;
    }

    private static string FormatNestedInstaller(PlannedInstaller? installer)
        => installer is null
            ? ""
            : string.Join(
                '|',
                installer.NestedInstallerFiles
                    .OrderBy(static file => file.RelativeFilePath, StringComparer.Ordinal)
                    .ThenBy(static file => file.PortableCommandAlias, StringComparer.Ordinal)
                    .Select(static file => $"{file.RelativeFilePath}=>{file.PortableCommandAlias}"));

    private static string FormatNestedShape(AnalyzedInstallerShape shape)
        => string.Join(
            '|',
            shape.NestedInstallerFiles
                .OrderBy(static file => file.RelativeFilePath, StringComparer.Ordinal)
                .ThenBy(static file => file.PortableCommandAlias, StringComparer.Ordinal)
                .Select(static file => $"{file.RelativeFilePath}=>{file.PortableCommandAlias}"));

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
        InstallerType? nestedType,
        Scope? scope,
        LanguageTag? installerLocale,
        string? displayVersion,
        string? entry,
        bool hasExplicitArchitecture,
        bool hasExplicitType,
        bool hasExplicitScope,
        EvidenceConfidence confidence,
        AnalyzedInstallerShape? analyzedShape)
    {
        public DiscoveredAsset Asset { get; } = asset;

        public Architecture? Architecture { get; } = architecture;

        public InstallerType? Type { get; } = type;

        public InstallerType? NestedType { get; } = nestedType;

        public Scope? Scope { get; } = scope;

        public LanguageTag? InstallerLocale { get; } = installerLocale;

        public string? DisplayVersion { get; } = displayVersion;

        public string? Entry { get; } = entry;

        public bool HasExplicitArchitecture { get; } = hasExplicitArchitecture;

        public bool HasExplicitType { get; } = hasExplicitType;

        public bool HasExplicitScope { get; } = hasExplicitScope;

        public EvidenceConfidence Confidence { get; } = confidence;

        public AnalyzedInstallerShape? AnalyzedShape { get; } = analyzedShape;

        public bool ClearsNestedState => Asset.Analysis is not null
            && Asset.Analysis.Format != DetectedInstallerFormat.Zip
            && Type != InstallerType.Zip;
    }
}
