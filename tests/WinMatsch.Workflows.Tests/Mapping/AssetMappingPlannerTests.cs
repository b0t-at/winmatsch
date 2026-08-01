using System.Collections.Immutable;
using WinMatsch.Analysis;
using WinMatsch.Core;
using WinMatsch.Downloads;
using WinMatsch.Rules.OverridePacks;
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
            Architecture.X64);
        candidate = candidate with
        {
            Analysis = BoundAnalysis(
                candidate,
                DetectedInstallerFormat.Zip,
                InstallerType.Zip,
                Architecture.X64,
                archiveEntries: ["tool-2.0.0/bin/tool.exe"],
                nestedCandidates: ["tool-2.0.0/bin/tool.exe"],
                archiveBinariesDependOnPath: true),
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
        DiscoveredAsset asset = Asset("tool-x64.zip", InstallerType.Zip, Architecture.X64);
        asset = asset with
        {
            Analysis = BoundAnalysis(
                asset,
                DetectedInstallerFormat.Zip,
                InstallerType.Zip,
                Architecture.X64,
                archiveEntries: ["../escape/tool.exe"],
                nestedCandidates: ["../escape/tool.exe"]),
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
        DiscoveredAsset candidate = Asset("tool-2.0.0-x64.zip", InstallerType.Zip, Architecture.X64);
        candidate = candidate with
        {
            Analysis = BoundAnalysis(
                candidate,
                DetectedInstallerFormat.Zip,
                InstallerType.Zip,
                Architecture.X64,
                archiveEntries: ["a/tool.exe", "b/tool.exe"],
                nestedCandidates: ["a/tool.exe", "b/tool.exe"]),
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([candidate], [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "NESTED_DUPLICATE");
    }

    [Fact]
    public void Unresolved_version_blocks_existing_mapping_without_throwing()
    {
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64);
        PreviousInstallerEntry previous = Previous(
            0,
            asset.DownloadUri.AbsoluteUri,
            Architecture.X64,
            InstallerType.Exe);
        AssetMappingRequest request = Request([asset], [previous]) with
        {
            Version = new(null, null, EvidenceConfidence.Low, false, [], ["VERSION_UNRESOLVED"]),
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(request);

        Assert.False(plan.CanApply);
        Assert.Equal(AssetMappingDecisionKind.Unresolved, Assert.Single(plan.Decisions).Kind);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "VERSION_BLOCKS_MAPPING");
    }

    [Fact]
    public void Analysis_must_be_bound_to_downloaded_content_identity()
    {
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64);
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                AnalyzedContentIdentity = new DownloadContentIdentity(Hash(999), 1),
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "ANALYSIS_IDENTITY_MISMATCH");
    }

    [Fact]
    public void Tokenless_new_asset_is_not_assigned_from_sibling_coverage()
    {
        DiscoveredAsset x64 = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64);
        DiscoveredAsset tokenless = Asset("tool-setup.exe", InstallerType.Exe, Architecture.X86);
        tokenless = tokenless with
        {
            Analysis = BoundAnalysis(
                tokenless,
                DetectedInstallerFormat.GenericInstallerExe,
                InstallerType.Exe,
                architecture: null),
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([x64, tokenless]));

        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.UnresolvedQuestions,
            question => question.AssetUrl == tokenless.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public void Correlated_analyzer_shapes_fan_out_scope_variants()
    {
        DiscoveredAsset asset = Asset("tool-x64.msi", InstallerType.Msi, Architecture.X64);
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Msi,
                        Scope = Scope.User,
                    },
                    new()
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Msi,
                        Scope = Scope.Machine,
                    },
                ],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        Assert.True(plan.CanApply);
        Assert.Equal(
            [Scope.User, Scope.Machine],
            plan.Decisions.Select(static decision => decision.Installer!.Scope).Order());
    }

    [Fact]
    public void Correlated_multi_architecture_shapes_do_not_conflict_with_each_other()
    {
        DiscoveredAsset asset = Asset("tool.msixbundle", InstallerType.Msix, Architecture.X64);
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = Architecture.X86,
                        InstallerType = InstallerType.Msix,
                    },
                    new()
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Msix,
                    },
                ],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        Assert.True(plan.CanApply);
        Assert.Equal(
            new[] { Architecture.X86, Architecture.X64 },
            plan.Decisions.Select(static decision => decision.Installer!.Architecture).Order().ToArray());
    }

    [Fact]
    public void Forced_architecture_does_not_authorize_unrelated_type_rewrite()
    {
        PackageIdentifier package = new("Vendor.Product");
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/tool.exe",
            Architecture.X64,
            InstallerType.Exe);
        DiscoveredAsset asset = Asset("tool-x64-portable.exe", InstallerType.Portable, Architecture.X64);
        var packs = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = package,
                ForcedArchitectures =
                [
                    new()
                    {
                        AssetPattern = "*",
                        Architecture = Architecture.X64,
                        SourceEvidence = "test",
                    },
                ],
            },
        ]);
        AssetMappingRequest request = Request([asset], [previous]) with
        {
            PackageIdentifier = package,
            OverridePacks = packs,
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(request);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_STRUCTURAL_REWRITE");
    }

    [Fact]
    public void Analyzer_selected_multiple_nested_files_are_preserved()
    {
        DiscoveredAsset asset = Asset("tool-x64.zip", InstallerType.Zip, Architecture.X64);
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Zip,
                        NestedInstallerType = InstallerType.Portable,
                        NestedInstallerFiles =
                        [
                            new("bin/tool.exe", "tool"),
                            new("bin/helper.exe", "tool-helper"),
                        ],
                    },
                ],
                ArchiveEntries = ["bin/tool.exe", "bin/helper.exe"],
                NestedInstallerCandidates = ["bin/tool.exe", "bin/helper.exe"],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]));

        PlannedInstaller installer = Assert.Single(plan.Decisions).Installer!;
        Assert.True(plan.CanApply);
        Assert.Equal(InstallerType.Portable, installer.NestedInstallerType);
        Assert.Equal(2, installer.NestedInstallerFiles.Length);
    }

    [Fact]
    public void Changed_hash_at_stable_url_requires_explicit_approval()
    {
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64);
        PreviousInstallerEntry previous = Previous(
            0,
            asset.DownloadUri.AbsoluteUri,
            Architecture.X64,
            InstallerType.Exe);

        AssetMappingPlan blocked = AssetMappingPlanner.CreatePlan(Request([asset], [previous]));
        AssetMappingPlan approved = AssetMappingPlanner.CreatePlan(Request([asset], [previous]) with
        {
            AllowStableUrlContentChange = true,
        });

        Assert.False(blocked.CanApply);
        Assert.Contains(blocked.Diagnostics, static diagnostic => diagnostic.Code == "CONTENT_CHANGED_AT_STABLE_URL");
        Assert.True(approved.CanApply);
    }

    [Fact]
    public void Same_extension_changed_url_requires_fresh_analysis()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/1.0.0/tool-x64.exe",
            Architecture.X64,
            InstallerType.Exe);
        DiscoveredAsset asset = Asset("tool-x64.exe", InstallerType.Exe, Architecture.X64) with
        {
            Analysis = null,
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset], [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_REANALYSIS_REQUIRED");
    }

    [Fact]
    public void Nested_installer_type_rewrite_requires_structural_approval()
    {
        PreviousInstallerEntry previous = Previous(
            0,
            "https://old.test/1.0.0/tool-x64.zip",
            Architecture.X64,
            InstallerType.Zip) with
        {
            NestedInstallerType = InstallerType.Portable,
            NestedInstallerFiles = [new("tool.exe", "tool")],
        };
        DiscoveredAsset asset = Asset("tool-x64.zip", InstallerType.Zip, Architecture.X64);
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = Architecture.X64,
                        InstallerType = InstallerType.Zip,
                        NestedInstallerType = InstallerType.Exe,
                        NestedInstallerFiles = [new("tool.exe", null)],
                    },
                ],
                ArchiveEntries = ["tool.exe"],
                NestedInstallerCandidates = ["tool.exe"],
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset], [previous]));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_STRUCTURAL_REWRITE");
    }

    [Fact]
    public void New_asset_with_mismatched_filename_version_blocks_apply()
    {
        DiscoveredAsset asset = Asset("tool-1.5.0-x64.exe", InstallerType.Exe, Architecture.X64);

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset], version: "2.0.0"));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_VERSION_DISCONTINUITY");
    }

    [Fact]
    public void Filename_version_mismatch_is_not_masked_by_release_path()
    {
        DiscoveredAsset asset = Asset("tool-1.5.0-x64.exe", InstallerType.Exe, Architecture.X64);
        asset = asset with
        {
            DownloadUri = new("https://example.test/download/2.0.0/tool-1.5.0-x64.exe"),
            Content = asset.Content! with
            {
                InitialUrl = "https://example.test/download/2.0.0/tool-1.5.0-x64.exe",
                FinalUrl = "https://example.test/download/2.0.0/tool-1.5.0-x64.exe",
            },
        };
        asset = asset with
        {
            Analysis = asset.Analysis! with
            {
                AnalyzedUrl = asset.Content!.FinalUrl,
            },
        };

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset], version: "2.0.0"));

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Diagnostics, static diagnostic => diagnostic.Code == "MAP_VERSION_DISCONTINUITY");
    }

    [Fact]
    public void Asset_mapping_override_resolves_architecture_conflict()
    {
        PackageIdentifier package = new("Vendor.Product");
        DiscoveredAsset asset = Asset(
            "tool-x64-arm64.exe",
            InstallerType.Exe,
            Architecture.X64);
        var packs = new OverridePackSet(
        [
            new OverridePack
            {
                PackageIdentifier = package,
                AssetMappings =
                [
                    new()
                    {
                        AssetPattern = "*",
                        Entry = "installer",
                        Architecture = Architecture.Arm64,
                        InstallerType = InstallerType.Exe,
                    },
                ],
            },
        ]);

        AssetMappingPlan plan = AssetMappingPlanner.CreatePlan(Request([asset]) with
        {
            PackageIdentifier = package,
            OverridePacks = packs,
        });

        Assert.True(plan.CanApply);
        Assert.Equal(Architecture.Arm64, Assert.Single(plan.Decisions).Installer?.Architecture);
        Assert.Contains(
            plan.Diagnostics,
            static diagnostic => diagnostic.Code == "ARCH_CONFLICT"
                && diagnostic.Severity == AssetMappingDiagnosticSeverity.Warning);
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
        var identity = new DownloadContentIdentity(Hash(name.Length), 1);
        var content = new AssetContentEvidence(
            identity,
            url.AbsoluteUri,
            url.AbsoluteUri,
            "application/octet-stream",
            DateTimeOffset.UnixEpoch);
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
            Content = content,
            Analysis = new AssetAnalysisEvidence
            {
                Format = type switch
                {
                    InstallerType.Zip => DetectedInstallerFormat.Zip,
                    InstallerType.Portable => DetectedInstallerFormat.PortableExe,
                    _ => DetectedInstallerFormat.GenericInstallerExe,
                },
                AnalyzedContentIdentity = identity,
                AnalyzedUrl = url.AbsoluteUri,
                InstallerShapes =
                [
                    new()
                    {
                        Architecture = architecture,
                        InstallerType = type,
                    },
                ],
            },
        };
    }

    private static AssetAnalysisEvidence BoundAnalysis(
        DiscoveredAsset asset,
        DetectedInstallerFormat format,
        InstallerType type,
        Architecture? architecture,
        ImmutableArray<string> archiveEntries = default,
        ImmutableArray<string> nestedCandidates = default,
        bool? archiveBinariesDependOnPath = null)
        => new()
        {
            Format = format,
            AnalyzedContentIdentity = asset.Content!.Identity,
            AnalyzedUrl = asset.Content.FinalUrl,
            InstallerShapes =
            [
                new()
                {
                    Architecture = architecture,
                    InstallerType = type,
                    ArchiveBinariesDependOnPath = archiveBinariesDependOnPath,
                },
            ],
            ArchiveEntries = archiveEntries.IsDefault ? [] : archiveEntries,
            NestedInstallerCandidates = nestedCandidates.IsDefault ? [] : nestedCandidates,
            ArchiveBinariesDependOnPath = archiveBinariesDependOnPath,
        };

    private static Sha256Hash Hash(int seed) => new(seed.ToString("X64"));
}
