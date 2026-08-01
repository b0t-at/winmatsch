using System.Buffers.Binary;
using System.IO.Compression;
using WinMatsch.Core;
using WinMatsch.Downloads;
using Xunit;

namespace WinMatsch.Validation.Tests;

public sealed class PreflightGateTests
{
    [Fact]
    public async Task Valid_gate_probes_urls_revalidates_hash_and_then_invokes_boundary()
    {
        var events = new List<string>();
        var network = new FakePreflightNetwork(events);
        var boundary = new FakeBoundary(events);
        var gate = new PreflightGate(network);

        ValidationReport report = await gate.ExecuteAsync(
            TestPackageFactory.CreateRequest(),
            boundary);

        Assert.True(report.IsValid, report.ToText());
        Assert.Equal(2, network.ProbeCount);
        Assert.Equal(1, network.RevalidationCount);
        Assert.Equal(1, boundary.InvocationCount);
        Assert.Equal(
            [
                $"probe:{TestPackageFactory.PublisherUrl}",
                $"probe:{TestPackageFactory.InstallerUrl}",
                $"revalidate:{TestPackageFactory.InstallerUrl}",
                "boundary",
            ],
            events);
    }

    [Fact]
    public async Task Failed_schema_gate_cannot_invoke_boundary()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument[] documents =
        [
            .. valid.Documents.Select(document =>
                document.RepositoryPath.EndsWith(".yaml", StringComparison.Ordinal)
                    && !document.RepositoryPath.Contains(".installer.", StringComparison.Ordinal)
                    && !document.RepositoryPath.Contains(".locale.", StringComparison.Ordinal)
                    ? document with
                    {
                        Content = document.Content.Replace(
                            "DefaultLocale: en-US",
                            "DefaultLocale:",
                            StringComparison.Ordinal),
                    }
                    : document),
        ];
        var request = Copy(valid, documents: documents);
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ExecuteAsync(request, boundary);

        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, static finding => finding.Code == "VLD1003");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Actual_preflight_header_path_returns_a_diagnostic_for_non_mapping_yaml()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument first = valid.Documents[0];
        ManifestDocument[] documents =
        [
            first with { Content = "- not\n- a\n- manifest\n" },
            .. valid.Documents.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents: documents));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD1001");
    }

    [Fact]
    public async Task Complete_package_set_and_cross_file_identity_are_required()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument[] incomplete =
        [
            .. valid.Documents.Where(static document =>
                !document.RepositoryPath.Contains(".locale.", StringComparison.Ordinal)),
        ];
        ManifestDocument[] mismatched =
        [
            .. valid.Documents.Select(document =>
                document.RepositoryPath.Contains(".installer.", StringComparison.Ordinal)
                    ? document with
                    {
                        Content = document.Content.Replace(
                            "PackageIdentifier: Example.App",
                            "PackageIdentifier: example.App",
                            StringComparison.Ordinal),
                    }
                    : document),
        ];

        ValidationReport missingReport = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents: incomplete));
        ValidationReport identityReport = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents: mismatched));

        Assert.Contains(missingReport.Findings, static finding => finding.Code == "VLD2004");
        Assert.Contains(identityReport.Findings, static finding => finding.Code == "VLD2103");
    }

    [Fact]
    public async Task Exact_pinned_schema_header_is_required()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument first = valid.Documents[0];
        ManifestDocument[] documents =
        [
            first with
            {
                Content = first.Content.Replace(
                    ".1.12.0.schema.json",
                    ".1.11.0.schema.json",
                    StringComparison.Ordinal),
            },
            .. valid.Documents.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents: documents));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD2104");
    }

    [Fact]
    public async Task Changed_origin_hash_is_hard_blocking()
    {
        var network = new FakePreflightNetwork { ReturnChangedContent = true };
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(network)
            .ExecuteAsync(TestPackageFactory.CreateRequest(), boundary);

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD6008");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Manifest_hash_mismatch_is_hard_blocking()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
        var wrongHash = new Sha256Hash(
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        var request = Copy(
            valid,
            artifacts:
            [
                artifact with
                {
                    Download = TestPackageFactory.CreateDownload(artifact.InstallerUrl, wrongHash),
                },
            ]);
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ExecuteAsync(request, boundary);

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD6007");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Duplicate_effective_installer_key_is_hard_blocking()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Installers!.Add(new Installer
        {
            Architecture = Architecture.X64,
            InstallerUrl = "https://example.com/other.exe",
            InstallerSha256 = new Sha256Hash(TestPackageFactory.Hash),
        });
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ExecuteAsync(TestPackageFactory.CreateRequest(manifests), boundary);

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3001");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Same_url_with_incompatible_effective_semantics_is_hard_blocking()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Installers!.Add(new Installer
        {
            Architecture = Architecture.Arm64,
            Scope = Scope.User,
            InstallerUrl = TestPackageFactory.InstallerUrl,
            InstallerSha256 = new Sha256Hash(TestPackageFactory.Hash),
        });
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ExecuteAsync(TestPackageFactory.CreateRequest(manifests), boundary);

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3002");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Same_url_user_machine_switch_twins_are_valid()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.Scope = null;
        List<Installer> installers = manifests.Installer.Installers!;
        Installer user = Assert.Single(installers);
        user.Scope = Scope.User;
        user.InstallerSwitches = new InstallerSwitches { Custom = "/CURRENTUSER" };
        installers.Add(new Installer
        {
            Architecture = Architecture.Arm64,
            Scope = Scope.Machine,
            InstallerUrl = TestPackageFactory.InstallerUrl,
            InstallerSha256 = new Sha256Hash(TestPackageFactory.Hash),
            InstallerSwitches = new InstallerSwitches { Custom = "/ALLUSERS" },
        });

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests));

        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3002");
        Assert.True(report.IsValid, report.ToText());
    }

    [Fact]
    public async Task Nested_paths_are_safe_and_aliases_may_repeat_across_alternative_installers()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Zip;
        List<Installer> installers = manifests.Installer.Installers!;
        Installer first = Assert.Single(installers);
        first.NestedInstallerType = InstallerType.Portable;
        first.NestedInstallerFiles =
        [
            new NestedInstallerFile
            {
                RelativeFilePath = "../tool.exe",
                PortableCommandAlias = "tool",
            },
        ];
        installers.Add(new Installer
        {
            Architecture = Architecture.Arm64,
            InstallerUrl = "https://example.com/setup-arm64.zip",
            InstallerSha256 = new Sha256Hash(TestPackageFactory.Hash),
            NestedInstallerType = InstallerType.Portable,
            NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "TOOL",
                },
            ],
        });

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3005");
        Assert.DoesNotContain(report.Findings, static finding => finding.Code == "VLD3010");
    }

    [Fact]
    public async Task Arp_display_version_overlap_with_existing_version_is_hard_blocking()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        Assert.Single(manifests.Installer.Installers!).AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry { DisplayVersion = "2026.7" },
        ];
        ExistingVersionSnapshot[] existing =
        [
            new("1.0.0", ["2026.7"]),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests, existingVersions: existing));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3101");
    }

    [Fact]
    public async Task Off_target_or_empty_diff_is_hard_blocking()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        var offTarget = Copy(
            valid,
            changes:
            [
                new RepositoryFileChange(
                    "manifests/o/Other/App/1.0.0/Other.App.yaml",
                    RepositoryChangeKind.Added),
            ]);
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ExecuteAsync(offTarget, boundary);
        ValidationReport empty = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, changes: []));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD4003");
        Assert.Contains(empty.Findings, static finding => finding.Code == "VLD4001");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Exact_repository_path_and_filename_casing_is_required()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        ManifestDocument first = valid.Documents[0];
        string wrongPath = first.RepositoryPath.Replace("Example", "example", StringComparison.Ordinal);
        ManifestDocument[] documents =
        [
            first with { RepositoryPath = wrongPath },
            .. valid.Documents.Skip(1),
        ];
        RepositoryFileChange[] changes =
        [
            new(wrongPath, RepositoryChangeKind.Added),
            .. valid.Changes.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, documents: documents, changes: changes));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD2202");
    }

    [Fact]
    public async Task Warning_policy_controls_probe_warnings()
    {
        PreflightRequest allow = TestPackageFactory.CreateRequest();
        var network = new FakePreflightNetwork
        {
            FailingProbeUrl = TestPackageFactory.PublisherUrl,
        };
        var allowedBoundary = new FakeBoundary();
        ValidationReport allowed = await new PreflightGate(network)
            .ExecuteAsync(allow, allowedBoundary);

        PreflightRequest strict = TestPackageFactory.CreateRequest(
            options: new PreflightOptions { WarningPolicy = WarningPolicy.TreatAsErrors });
        var strictBoundary = new FakeBoundary();
        ValidationReport blocked = await new PreflightGate(
            new FakePreflightNetwork
            {
                FailingProbeUrl = TestPackageFactory.PublisherUrl,
            }).ExecuteAsync(strict, strictBoundary);

        Assert.True(allowed.IsValid);
        Assert.Contains(allowed.Findings, static finding => finding.Code == "VLD5005");
        Assert.Equal(1, allowedBoundary.InvocationCount);
        Assert.True(blocked.IsValid);
        Assert.False(blocked.CanProceed(WarningPolicy.TreatAsErrors));
        Assert.Equal(0, strictBoundary.InvocationCount);
    }

    [Fact]
    public async Task Installer_probe_failure_is_always_hard_blocking()
    {
        var network = new FakePreflightNetwork
        {
            FailingProbeUrl = TestPackageFactory.InstallerUrl,
        };
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(network)
            .ExecuteAsync(TestPackageFactory.CreateRequest(), boundary);

        ValidationFinding finding = Assert.Single(
            report.Findings,
            static finding => finding.Code == "VLD5004");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Arp_display_version_ranges_cannot_overlap()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        Installer first = Assert.Single(manifests.Installer.Installers!);
        first.AppsAndFeaturesEntries =
        [
            new AppsAndFeaturesEntry { DisplayVersion = "1.0" },
            new AppsAndFeaturesEntry { DisplayVersion = "3.0" },
        ];
        ExistingVersionSnapshot[] existing =
        [
            new("1.5.0", ["2.0"]),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests, existingVersions: existing));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3101");
    }

    [Fact]
    public async Task Deleted_diff_path_cannot_remain_in_post_change_documents()
    {
        PreflightRequest valid = TestPackageFactory.CreateRequest();
        RepositoryFileChange deleted = valid.Changes[0] with
        {
            Kind = RepositoryChangeKind.Deleted,
        };
        RepositoryFileChange[] changes =
        [
            deleted,
            .. valid.Changes.Skip(1),
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(Copy(valid, changes: changes));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD4004");
    }

    [Fact]
    public async Task Nested_installer_path_must_exist_in_downloaded_archive_with_exact_casing()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("bin/tool.exe");
            }

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "Bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            DownloadResult download = TestPackageFactory.CopyDownloadForFile(
                artifact.Download,
                archivePath);
            PreflightRequest request = Copy(
                valid,
                artifacts: [artifact with { Download = download }]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.Contains(report.Findings, static finding => finding.Code == "VLD3011");
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Nested_membership_is_bound_to_the_recorded_artifact_identity()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("bin/tool.exe");
            }

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath,
                            hashOverride: artifact.Download.Sha256),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.Contains(
                report.Findings,
                static finding => finding.Code == "VLD3012"
                    && finding.Message.Contains("SHA-256 changed", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Lowercase_recorded_hash_matches_the_same_archive_identity()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("bin/tool.exe");
            }

            DownloadResult actual = TestPackageFactory.CopyDownloadForFile(
                TestPackageFactory.CreateDownload(
                    TestPackageFactory.InstallerUrl,
                    new Sha256Hash(TestPackageFactory.Hash)),
                archivePath);
            var lowercaseHash = new Sha256Hash(actual.Sha256.Value.ToLowerInvariant());
            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.InstallerSha256 = lowercaseHash;
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath,
                            hashOverride: lowercaseHash),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.True(report.IsValid, report.ToText());
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Root_nested_metadata_can_be_shared_by_alternative_architectures()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("bin/tool.exe");
            }

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            manifests.Installer.NestedInstallerType = InstallerType.Portable;
            manifests.Installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            var archiveHash = TestPackageFactory.CopyDownloadForFile(
                TestPackageFactory.CreateDownload(
                    TestPackageFactory.InstallerUrl,
                    new Sha256Hash(TestPackageFactory.Hash)),
                archivePath).Sha256;
            foreach (Installer installer in manifests.Installer.Installers!)
            {
                installer.InstallerSha256 = archiveHash;
            }

            manifests.Installer.Installers!.Add(new Installer
            {
                Architecture = Architecture.Arm64,
                InstallerUrl = TestPackageFactory.InstallerUrl,
                InstallerSha256 = archiveHash,
            });
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.True(report.IsValid, report.ToText());
            Assert.DoesNotContain(
                report.Findings,
                static finding => finding.Code is "VLD3006" or "VLD3010");
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Zip_entry_count_is_rejected_before_central_directory_materialization()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            byte[] endRecord = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(endRecord, 0x06054B50);
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(8), 10_001);
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(10), 10_001);
            await File.WriteAllBytesAsync(archivePath, endRecord);

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.Contains(
                report.Findings,
                static finding => finding.Code == "VLD3012"
                    && finding.Message.Contains("10001 entries", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Windows_equivalent_archive_paths_are_rejected_as_ambiguous()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("bin/tool.exe");
                _ = archive.CreateEntry("BIN\\tool.exe");
            }

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.Contains(
                report.Findings,
                static finding => finding.Code == "VLD3012"
                    && finding.Message.Contains("collide on Windows", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Traversal_directory_entries_are_rejected_before_membership_checks()
    {
        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"winmatsch-validation-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                _ = archive.CreateEntry("../");
                _ = archive.CreateEntry("bin/tool.exe");
            }

            PackageManifests manifests = TestPackageFactory.CreateManifests();
            manifests.Installer.InstallerType = InstallerType.Zip;
            Installer installer = Assert.Single(manifests.Installer.Installers!);
            installer.NestedInstallerType = InstallerType.Portable;
            installer.NestedInstallerFiles =
            [
                new NestedInstallerFile
                {
                    RelativeFilePath = "bin/tool.exe",
                    PortableCommandAlias = "tool",
                },
            ];
            PreflightRequest valid = TestPackageFactory.CreateRequest(manifests);
            InstallerArtifact artifact = Assert.Single(valid.InstallerArtifacts);
            PreflightRequest request = Copy(
                valid,
                artifacts:
                [
                    artifact with
                    {
                        Download = TestPackageFactory.CopyDownloadForFile(
                            artifact.Download,
                            archivePath),
                    },
                ]);

            ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
                .ValidateAsync(request);

            Assert.Contains(
                report.Findings,
                static finding => finding.Code == "VLD3012"
                    && finding.Message.Contains(
                        "not a safe Windows-relative path",
                        StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData(".")]
    [InlineData("tool*")]
    [InlineData("bad\u0001alias")]
    public async Task Portable_aliases_must_be_safe_windows_command_names(string alias)
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        manifests.Installer.InstallerType = InstallerType.Zip;
        Installer installer = Assert.Single(manifests.Installer.Installers!);
        installer.NestedInstallerType = InstallerType.Portable;
        installer.NestedInstallerFiles =
        [
            new NestedInstallerFile
            {
                RelativeFilePath = "bin/tool.exe",
                PortableCommandAlias = alias,
            },
        ];

        ValidationReport report = await new PreflightGate(new FakePreflightNetwork())
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests));

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD3009");
    }

    [Fact]
    public async Task Changed_redirect_target_blocks_boundary_even_when_revalidation_reports_unchanged()
    {
        var network = new FakePreflightNetwork
        {
            RevalidatedFinalUrl = "https://cdn2.example.com/setup.exe",
        };
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate(network)
            .ExecuteAsync(TestPackageFactory.CreateRequest(), boundary);

        Assert.Contains(report.Findings, static finding => finding.Code == "VLD6011");
        Assert.Equal(0, boundary.InvocationCount);
    }

    [Fact]
    public async Task Downloader_policy_failure_becomes_a_deterministic_probe_error()
    {
        PackageManifests manifests = TestPackageFactory.CreateManifests();
        const string insecureUrl = "http://example.com/setup.exe";
        Assert.Single(manifests.Installer.Installers!).InstallerUrl = insecureUrl;
        var network = new FakePreflightNetwork
        {
            InvalidOperationProbeUrl = insecureUrl,
        };

        ValidationReport report = await new PreflightGate(network)
            .ValidateAsync(TestPackageFactory.CreateRequest(manifests));

        ValidationFinding finding = Assert.Single(
            report.Findings,
            static finding => finding.Code == "VLD5004");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
    }

    [Theory]
    [InlineData(NetworkValidationMode.Offline, "VLD5001")]
    [InlineData(NetworkValidationMode.Skip, "VLD5002")]
    public async Task Offline_and_skipped_probe_modes_are_explicit_and_cannot_bypass_revalidation(
        NetworkValidationMode mode,
        string probeCode)
    {
        PreflightRequest request = TestPackageFactory.CreateRequest(
            options: new PreflightOptions { NetworkMode = mode });
        var boundary = new FakeBoundary();

        ValidationReport report = await new PreflightGate()
            .ExecuteAsync(request, boundary);

        Assert.Contains(report.Findings, finding => finding.Code == probeCode);
        Assert.Contains(report.Findings, static finding => finding.Code == "VLD6001");
        Assert.Equal(0, boundary.InvocationCount);
    }

    private static PreflightRequest Copy(
        PreflightRequest source,
        IReadOnlyList<ManifestDocument>? documents = null,
        IReadOnlyList<RepositoryFileChange>? changes = null,
        IReadOnlyList<InstallerArtifact>? artifacts = null)
        => new()
        {
            Documents = documents ?? source.Documents,
            Changes = changes ?? source.Changes,
            ExistingVersions = source.ExistingVersions,
            InstallerArtifacts = artifacts ?? source.InstallerArtifacts,
            Options = source.Options,
        };

}
