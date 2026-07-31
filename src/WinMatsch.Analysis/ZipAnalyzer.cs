using System.IO.Compression;
using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// Analyzes ZIP archives from their current contents, validates candidate magic and paths,
/// and resolves single-type multi-architecture archives without carrying stale nested paths.
/// </summary>
public sealed class ZipAnalyzer : IInstallerAnalyzer
{
    private static readonly string[] _skippedFolderNames = ["__MACOSX", "resources"];
    private static ReadOnlySpan<byte> CompoundFileMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public bool CanAnalyze(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public InstallerAnalysis Analyze(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using IDisposable scope = AnalysisLimits.EnterArchive($"'{fileName}'");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        AnalysisLimits.ValidateArchive(archive, $"'{fileName}'");

        List<Candidate> candidates = [];
        List<string> rejectedCandidates = [];
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = NormalizeAndValidatePath(entry.FullName);
            if (path.EndsWith('/'))
            {
                continue;
            }

            if (!filePaths.Add(path))
            {
                throw new InvalidDataException(
                    $"The archive contains duplicate entry path '{path}' after normalization. Manual analysis is required.");
            }

            InstallerType? nestedType = MapNestedInstallerType(path);
            if (nestedType is null || IsInSkippedFolder(path))
            {
                continue;
            }

            if (!HasExpectedMagic(entry, nestedType.Value))
            {
                rejectedCandidates.Add(path);
                continue;
            }

            candidates.Add(new Candidate(path, entry, nestedType.Value));
        }

        if (candidates.Count == 0)
        {
            if (rejectedCandidates.Count > 0)
            {
                throw new InvalidDataException(
                    $"The archive contains installable-looking paths whose content magic does not match their extensions: "
                    + $"{string.Join(", ", rejectedCandidates)}. Manual analysis is required.");
            }

            throw new InvalidDataException(
                "The archive contains no installable payloads (no valid .msi, .msix, .appx, .exe, .msixbundle or .appxbundle entries).");
        }

        List<ResolvedCandidate> resolved = [];
        foreach (Candidate candidate in candidates)
        {
            resolved.AddRange(ResolveCandidate(candidate));
        }

        bool sameNestedType = resolved.Select(static candidate => candidate.NestedType).Distinct().Count() == 1;
        bool fullyArchitectured = resolved.All(static candidate => candidate.Architecture is not null);
        bool oneFilePerArchitecture = resolved
            .GroupBy(static candidate => (candidate.NestedType, candidate.Architecture))
            .All(static group => group.Select(static candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1);
        bool portableCollection = sameNestedType && resolved[0].NestedType == InstallerType.Portable;
        if (candidates.Count == 1
            || (sameNestedType && fullyArchitectured && (portableCollection || oneFilePerArchitecture)))
        {
            return BuildResolvedAnalysis(resolved, candidates.Select(static candidate => candidate.Path).ToArray());
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Zip,
            Installers = [new Installer { InstallerType = InstallerType.Zip }],
            Zip = new ZipContents([.. candidates.Select(static candidate => candidate.Path)]),
            Diagnostics =
            [
                new AnalysisDiagnostic(
                    "ZIP001",
                    "The archive contains multiple installer candidates with different or unresolved types. "
                        + "Select the intended nested path manually; no candidate was guessed.",
                    RequiresManualAnalysis: true),
            ],
        };
    }

    private static List<ResolvedCandidate> ResolveCandidate(Candidate candidate)
    {
        byte[] bytes = AnalysisLimits.ReadEntryBytes(candidate.Entry, $"Archive entry '{candidate.Path}'");
        using var buffer = new MemoryStream(bytes, writable: false);
        InstallerAnalysis inner;
        try
        {
            inner = FileAnalyzer.Analyze(buffer, Path.GetFileName(candidate.Path));
        }
        catch (Exception exception) when (exception is InvalidDataException or BadImageFormatException)
        {
            throw new InvalidDataException(
                $"Archive entry '{candidate.Path}' has matching magic but could not be analyzed safely. Manual analysis is required.",
                exception);
        }

        if (candidate.DeclaredType is InstallerType.Msix or InstallerType.Appx
            && inner.Format is not DetectedInstallerFormat.Msix and not DetectedInstallerFormat.MsixBundle)
        {
            throw new InvalidDataException(
                $"Archive entry '{candidate.Path}' has ZIP magic but lacks the required MSIX/AppX package manifest. "
                    + "Manual analysis is required.");
        }

        List<ResolvedCandidate> resolved = [];
        foreach (Installer innerInstaller in inner.Installers)
        {
            InstallerType nestedType = innerInstaller.NestedInstallerType
                ?? innerInstaller.InstallerType
                ?? candidate.DeclaredType;
            resolved.Add(new ResolvedCandidate(
                candidate.Path,
                nestedType,
                innerInstaller,
                inner.ProductName,
                inner.Publisher,
                inner.ProductVersion,
                inner.Copyright,
                inner.Diagnostics));
        }

        return resolved;
    }

    private static InstallerAnalysis BuildResolvedAnalysis(
        IReadOnlyList<ResolvedCandidate> resolved,
        IReadOnlyList<string> candidatePaths)
    {
        List<Installer> installers = [];
        foreach (IGrouping<(InstallerType NestedType, Architecture? Architecture), ResolvedCandidate> architectureGroup in resolved.GroupBy(
            static candidate => (candidate.NestedType, candidate.Architecture)))
        {
            foreach (List<ResolvedCandidate> group in PartitionCompatible(architectureGroup))
            {
                string[] paths = [.. group.Select(static candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase)];
                bool aliasesRequired = architectureGroup.Key.NestedType == InstallerType.Portable && paths.Length > 1;
                List<NestedInstallerFile> nestedFiles = [];
                var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    string? alias = aliasesRequired ? Path.GetFileNameWithoutExtension(path) : null;
                    if (alias is not null)
                    {
                        ValidatePortableAlias(alias, path);
                        if (!aliases.Add(alias))
                        {
                            throw new InvalidDataException(
                                $"Portable archive paths produce duplicate command alias '{alias}'. Manual alias selection is required.");
                        }
                    }

                    nestedFiles.Add(new NestedInstallerFile
                    {
                        RelativeFilePath = path,
                        PortableCommandAlias = alias,
                    });
                }

                Installer installer = group[0].Installer;
                installer.InstallerType = InstallerType.Zip;
                installer.NestedInstallerType = architectureGroup.Key.NestedType;
                installer.NestedInstallerFiles = nestedFiles;
                installers.Add(installer);
            }
        }

        List<AnalysisDiagnostic> diagnostics =
        [
            .. resolved.SelectMany(static candidate => candidate.Diagnostics).Distinct(),
        ];
        if (installers.Count > 1)
        {
            diagnostics.Add(new AnalysisDiagnostic(
                "ZIP002",
                "The archive contains payloads for multiple architectures or incompatible installer metadata; "
                    + "separate ZIP installer entries were produced."));
        }

        return new InstallerAnalysis
        {
            Format = DetectedInstallerFormat.Zip,
            Installers = installers,
            ProductName = CommonValue(resolved.Select(static candidate => candidate.ProductName)),
            Publisher = CommonValue(resolved.Select(static candidate => candidate.Publisher)),
            ProductVersion = CommonValue(resolved.Select(static candidate => candidate.ProductVersion)),
            Copyright = CommonValue(resolved.Select(static candidate => candidate.Copyright)),
            Zip = new ZipContents(candidatePaths),
            Diagnostics = diagnostics,
        };
    }

    private static string? CommonValue(IEnumerable<string?> values)
    {
        string[] present = [.. values.OfType<string>().Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)];
        return present.Length == 1 ? present[0] : null;
    }

    private static List<List<ResolvedCandidate>> PartitionCompatible(
        IEnumerable<ResolvedCandidate> candidates)
    {
        List<List<ResolvedCandidate>> groups = [];
        foreach (ResolvedCandidate candidate in candidates)
        {
            List<ResolvedCandidate>? compatible = groups.FirstOrDefault(
                group => HaveCompatibleMetadata(group[0].Installer, candidate.Installer));
            if (compatible is null)
            {
                groups.Add([candidate]);
            }
            else
            {
                compatible.Add(candidate);
            }
        }

        return groups;
    }

    private static bool HaveCompatibleMetadata(Installer left, Installer right)
        => left.ProductCode == right.ProductCode
            && Equals(left.Scope, right.Scope)
            && Equals(left.InstallerLocale, right.InstallerLocale)
            && Equals(left.ElevationRequirement, right.ElevationRequirement)
            && SwitchesEqual(left.InstallerSwitches, right.InstallerSwitches)
            && ArpEntriesEqual(left.AppsAndFeaturesEntries, right.AppsAndFeaturesEntries)
            && DependenciesEqual(left.Dependencies, right.Dependencies);

    private static bool SwitchesEqual(InstallerSwitches? left, InstallerSwitches? right)
        => ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && left.Silent == right.Silent
                && left.SilentWithProgress == right.SilentWithProgress
                && left.Interactive == right.Interactive
                && left.InstallLocation == right.InstallLocation
                && left.Log == right.Log
                && left.Upgrade == right.Upgrade
                && left.Custom == right.Custom
                && left.Repair == right.Repair);

    private static bool ArpEntriesEqual(
        List<AppsAndFeaturesEntry>? left,
        List<AppsAndFeaturesEntry>? right)
        => ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && left.Count == right.Count
                && left.Zip(right).All(static pair
                    => pair.First.DisplayName == pair.Second.DisplayName
                        && pair.First.Publisher == pair.Second.Publisher
                        && pair.First.DisplayVersion == pair.Second.DisplayVersion
                        && pair.First.ProductCode == pair.Second.ProductCode
                        && pair.First.UpgradeCode == pair.Second.UpgradeCode
                        && pair.First.InstallerType == pair.Second.InstallerType));

    private static bool DependenciesEqual(Dependencies? left, Dependencies? right)
        => ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && SequenceEqual(left.WindowsFeatures, right.WindowsFeatures)
                && SequenceEqual(left.WindowsLibraries, right.WindowsLibraries)
                && SequenceEqual(left.ExternalDependencies, right.ExternalDependencies)
                && PackageDependenciesEqual(left.PackageDependencies, right.PackageDependencies));

    private static bool PackageDependenciesEqual(
        List<PackageDependency>? left,
        List<PackageDependency>? right)
        => ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && left.Count == right.Count
                && left.Zip(right).All(static pair
                    => Equals(pair.First.PackageIdentifier, pair.Second.PackageIdentifier)
                        && Equals(pair.First.MinimumVersion, pair.Second.MinimumVersion)));

    private static bool SequenceEqual<T>(List<T>? left, List<T>? right)
        => ReferenceEquals(left, right)
            || (left is not null && right is not null && left.SequenceEqual(right));

    private static bool HasExpectedMagic(ZipArchiveEntry entry, InstallerType type)
    {
        Span<byte> prefix = stackalloc byte[8];
        using Stream stream = entry.Open();
        int read = stream.ReadAtLeast(prefix, 2, throwOnEndOfStream: false);
        ReadOnlySpan<byte> available = prefix[..read];
        return type switch
        {
            InstallerType.Exe => available.Length >= 2
                && available[0] == (byte)'M'
                && available[1] == (byte)'Z',
            InstallerType.Msi => available.Length >= 8
                && available[..8].SequenceEqual(CompoundFileMagic),
            InstallerType.Msix or InstallerType.Appx => available.Length >= 4
                && available[0] == (byte)'P'
                && available[1] == (byte)'K',
            _ => false,
        };
    }

    private static InstallerType? MapNestedInstallerType(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Msi;
        }

        if (string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".msixbundle", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Msix;
        }

        if (string.Equals(extension, ".appx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".appxbundle", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Appx;
        }

        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ? InstallerType.Exe : null;
    }

    private static string NormalizeAndValidatePath(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Any(char.IsControl))
        {
            throw new InvalidDataException("The archive contains an empty path or control characters in an entry name.");
        }

        string normalized = entryName.Replace('\\', '/');
        if (normalized.Length > AnalysisLimits.MaxArchivePathLength)
        {
            throw new InvalidDataException(
                $"The archive entry path exceeds the {AnalysisLimits.MaxArchivePathLength}-character analysis limit.");
        }

        if (normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new InvalidDataException(
                $"The archive entry '{entryName}' uses an absolute path; refusing to analyze a hostile archive.");
        }

        string[] segments = normalized.Split('/');
        int effectiveSegmentCount = normalized.EndsWith('/') ? segments.Length - 1 : segments.Length;
        if (effectiveSegmentCount > AnalysisLimits.MaxArchivePathDepth)
        {
            throw new InvalidDataException(
                $"The archive entry '{entryName}' exceeds the supported path depth of {AnalysisLimits.MaxArchivePathDepth}.");
        }

        for (int i = 0; i < effectiveSegmentCount; i++)
        {
            if (segments[i] is "" or "." or ".." || segments[i].Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The archive entry '{entryName}' contains an unsafe or ambiguous path segment.");
            }
        }

        return normalized;
    }

    private static void ValidatePortableAlias(string alias, string path)
    {
        if (alias.Length == 0
            || alias.Any(character => char.IsControl(character) || character is '/' or '\\' or ':'))
        {
            throw new InvalidDataException(
                $"Archive path '{path}' cannot produce a safe portable command alias. Manual alias selection is required.");
        }
    }

    private static bool IsInSkippedFolder(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            foreach (string skipped in _skippedFolderNames)
            {
                if (string.Equals(segments[i], skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Candidate(string Path, ZipArchiveEntry Entry, InstallerType DeclaredType);

    private sealed record ResolvedCandidate(
        string Path,
        InstallerType NestedType,
        Installer Installer,
        string? ProductName,
        string? Publisher,
        string? ProductVersion,
        string? Copyright,
        IReadOnlyList<AnalysisDiagnostic> Diagnostics)
    {
        public Architecture? Architecture => Installer.Architecture;
    }
}
