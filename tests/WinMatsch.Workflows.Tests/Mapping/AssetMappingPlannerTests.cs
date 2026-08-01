using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;
using WinMatsch.Workflows.Versioning;
using Xunit;

namespace WinMatsch.Workflows.Tests.Mapping;

public sealed class AssetMappingPlannerTests
{
    [Fact]
    public void Setup_and_portable_assets_remain_distinct()
    {
        ImmutableArray<DiscoveredAsset> assets =
        [
            Asset("tool-x64.exe", InstallerType.Nullsoft, Architecture.X64),
            Asset("tool-x64-portable.exe", InstallerType.Portable, Architecture.X64),
        ];

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request(assets));

        Assert.True(plan.CanApply);
        Assert.Equal(2, plan.Decisions.Count(static decision => decision.Kind == AssetMappingDecisionKind.Proposed));
        Assert.Equal(
            [InstallerType.Nullsoft, InstallerType.Portable],
            plan.Decisions.Select(static decision => decision.Installer?.InstallerType).Order());
    }

    [Fact]
    public void Duplicate_compatible_candidates_block_unattended_mapping()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool-x64.exe",
            Architecture.X64,
            InstallerType.Exe);
        ImmutableArray<DiscoveredAsset> assets =
        [
            Asset("tool-a-x64.exe", InstallerType.Exe, Architecture.X64),
            Asset("tool-b-x64.exe", InstallerType.Exe, Architecture.X64),
        ];

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request(assets, [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.UnresolvedQuestions, static question => question.Code == "MAP_AMBIGUOUS");
    }

    [Fact]
    public void Input_reordering_produces_identical_plan()
    {
        ImmutableArray<DiscoveredAsset> ordered =
        [
            Asset("tool-x86.zip", InstallerType.Zip, Architecture.X86),
            Asset("tool-x64.zip", InstallerType.Zip, Architecture.X64),
            Asset("tool-arm64.zip", InstallerType.Zip, Architecture.Arm64),
        ];

        AssetMappingPlan first = AssetMappingPlanner.CreatePlan(Request(ordered));
        AssetMappingPlan second = AssetMappingPlanner.CreatePlan(Request([.. ordered.Reverse()]));

        Assert.Equal(first.DeterministicKey, second.DeterministicKey);
    }

    [Fact]
    public void Packaging_change_without_analysis_blocks_apply()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool-x64.exe",
            Architecture.X64,
            InstallerType.Exe);
        DiscoveredAsset candidate = Asset(
            "tool-x64.zip",
            InstallerType.Zip,
            Architecture.X64) with
        {
            Analysis = null,
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([candidate], [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_PACKAGING_REANALYSIS_REQUIRED");
    }

    [Fact]
    public void Nested_paths_are_rederived_from_bounded_contents_and_version_templated()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool-1.0.0-x64.zip",
            Architecture.X64,
            InstallerType.Zip) with
        {
            PackageVersion = new("1.0.0"),
            NestedInstallerFiles = [new("tool-1.0.0/bin/tool.exe", "tool")],
        };
        DiscoveredAsset candidate = Asset(
            "tool-2.0.0-x64.zip",
            InstallerType.Zip,
            Architecture.X64) with
        {
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.Zip,
                InstallerTypes = [InstallerType.Zip],
                PayloadArchitectures = [Architecture.X64],
                ArchiveEntries = ["tool-2.0.0/bin/tool.exe"],
                NestedInstallerCandidates = ["tool-2.0.0/bin/tool.exe"],
                ArchiveBinariesDependOnPath = true,
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request(
            [candidate],
            [previous],
            version: "2.0.0"));

        PlannedInstaller installer = Assert.Single(plan.Decisions).Installer!;
        Assert.Equal("tool-2.0.0/bin/tool.exe", Assert.Single(installer.NestedInstallerFiles).RelativeFilePath);
        Assert.True(installer.ArchiveBinariesDependOnPath);
    }

    [Fact]
    public void Url_override_parser_accepts_documented_syntax_and_rejects_invalid_values()
    {
        UrlOverride result = UrlOverride.Parse("https://example.test/tool.exe|arm64|machine|2.0-preview");

        Assert.Equal(Architecture.Arm64, result.Architecture);
        Assert.Equal(Scope.Machine, result.Scope);
        Assert.Equal("2.0-preview", result.DisplayVersion);
        Assert.False(UrlOverride.TryParse("https://example.test/tool.exe|x64|invalid|2.0", out _, out _));
    }

    [Fact]
    public void Missing_content_identity_blocks_unattended_apply()
    {
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64) with
        {
            Content = null,
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "CONTENT_IDENTITY_MISSING");
    }

    [Fact]
    public void Hostile_archive_paths_block_unattended_apply()
    {
        DiscoveredAsset asset = Asset("tool-x64.zip", InstallerType.Zip, Architecture.X64) with
        {
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.Zip,
                InstallerTypes = [InstallerType.Zip],
                PayloadArchitectures = [Architecture.X64],
                ArchiveEntries = ["../escape/tool.exe"],
                NestedInstallerCandidates = ["../escape/tool.exe"],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "ANALYSIS_EVIDENCE_INVALID");
    }

    [Fact]
    public void Removed_previous_asset_is_flagged_and_blocks_apply()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool-x64.exe",
            Architecture.X64,
            InstallerType.Exe);

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([], [previous]));

        Assert.False(plan.CanApply);
        Assert.Equal(AssetMappingDecisionKind.Removed, Assert.Single(plan.Decisions).Kind);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_REMOVED");
    }

    [Fact]
    public void Duplicate_nested_aliases_block_apply()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool-1.0.0-x64.zip",
            Architecture.X64,
            InstallerType.Zip) with
        {
            NestedInstallerFiles =
            [
                new("a/tool.exe", "tool"),
                new("b/tool.exe", "tool"),
            ],
        };
        DiscoveredAsset candidate = Asset("tool-2.0.0-x64.zip", InstallerType.Zip, Architecture.X64) with
        {
            Analysis = new AssetAnalysisEvidence
            {
                Format = DetectedInstallerFormat.Zip,
                InstallerTypes = [InstallerType.Zip],
                PayloadArchitectures = [Architecture.X64],
                ArchiveEntries = ["a/tool.exe", "b/tool.exe"],
                NestedInstallerCandidates = ["a/tool.exe", "b/tool.exe"],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([candidate], [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "NESTED_DUPLICATE");
    }

    [Fact]
    public void Same_url_with_incompatible_types_is_rejected()
    {
        Uri shared = new("https://example.test/tool.exe");
        ImmutableArray<PreviousInstallerEntry> previous =
        [
            Previous(0, shared.AbsoluteUri, Architecture.X64, InstallerType.Exe),
            Previous(1, shared.AbsoluteUri, Architecture.X64, InstallerType.Portable),
        ];
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64) with
        {
            DownloadUri = shared,
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset], previous));

        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.Diagnostics,
            static diagnostic => diagnostic.Code == "MAP_SAME_URL_INCOMPATIBLE_TYPE");
    }

    private static AssetMappingRequest Request(
        ImmutableArray<DiscoveredAsset> assets,
        ImmutableArray<PreviousInstallerEntry> previous = default,
        string version = "2.0.0")
        => new()
        {
            PackageIdentifier = new("Vendor.Product"),
            Version = new(
                new PackageVersion(version),
                PackageVersionSource.PackageOverride,
                EvidenceConfidence.Explicit,
                false,
                [],
                []),
            Assets = assets,
            PreviousInstallers = previous.IsDefault ? [] : previous,
        };

    private static PreviousInstallerEntry Previous(
        int position,
        string url,
        Architecture architecture,
        InstallerType type)
        => new()
        {
            Position = position,
            Url = new(url),
            Sha256 = Hash(position + 1),
            Architecture = architecture,
            InstallerType = type,
            PackageVersion = new("1.0.0"),
        };

    private static DiscoveredAsset Asset(
        string name,
        InstallerType type,
        Architecture architecture)
    {
        Uri url = new($"https://example.test/2.0.0/{name}");
        return new()
        {
            ReleaseId = 1,
            ReleaseTag = "v2.0.0",
            ReleaseName = "2.0.0",
            ReleaseUri = new("https://example.test/releases/1"),
            IsPrerelease = false,
            ReleasePublishedAt = DateTimeOffset.UnixEpoch,
            AssetId = name.GetHashCode(StringComparison.Ordinal),
            AssetName = name,
            DownloadUri = url,
            DeclaredContentType = "application/octet-stream",
            DeclaredSize = 1,
            AssetCreatedAt = DateTimeOffset.UnixEpoch,
            Content = new(
                new DownloadContentIdentity(Hash(name.Length), 1),
                url.AbsoluteUri,
                url.AbsoluteUri,
                "application/octet-stream",
                DateTimeOffset.UnixEpoch),
            Analysis = new AssetAnalysisEvidence
            {
                Format = type switch
                {
                    InstallerType.Zip => DetectedInstallerFormat.Zip,
                    InstallerType.Portable => DetectedInstallerFormat.PortableExe,
                    _ => DetectedInstallerFormat.GenericInstallerExe,
                },
                InstallerTypes = [type],
                PayloadArchitectures = [architecture],
            },
        };
    }

    private static Sha256Hash Hash(int seed) => new(seed.ToString("X64"));
}
