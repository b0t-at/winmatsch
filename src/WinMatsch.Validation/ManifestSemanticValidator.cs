using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using WinMatsch.Core;
using WinMatsch.Core.Yaml;
using WinMatsch.Downloads;

namespace WinMatsch.Validation;

internal static class ManifestSemanticValidator
{
    private const int MaxArchiveEntries = 10_000;
    private const int MaxArchivePathLength = 2_048;
    private const long MaxArchiveEntryBytes = 256L * 1024 * 1024;
    private const long MaxExpandedArchiveBytes = 1024L * 1024 * 1024;
    private const long MaxCentralDirectoryBytes = 64L * 1024 * 1024;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;

    public static SemanticValidationResult Validate(
        ParsedPackage package,
        PreflightRequest request,
        List<ValidationFinding> findings)
    {
        PackageManifests manifests = package.Manifests;
        PackageIdentifier? identifier = manifests.Version.PackageIdentifier;
        PackageVersion? version = manifests.Version.PackageVersion;
        if (identifier is null || version is null)
        {
            return new SemanticValidationResult([], []);
        }

        string expectedDirectory = ManifestPaths.GetVersionDirectory(identifier, version);
        ValidateDocumentPaths(package, expectedDirectory, identifier, findings);
        ValidateDiff(request.Changes, package, expectedDirectory, findings);
        ValidateInstallers(manifests.Installer, findings);
        ValidateNestedArchiveContents(
            manifests.Installer,
            request.InstallerArtifacts,
            findings);
        ValidateArpOverlap(manifests, request.ExistingVersions, findings);

        IReadOnlyList<UrlTarget> urls = CollectUrls(manifests);
        IReadOnlyList<ExpectedInstallerHash> hashes = CollectInstallerHashes(manifests.Installer);
        return new SemanticValidationResult(urls, hashes);
    }

    private static void ValidateDocumentPaths(
        ParsedPackage package,
        string expectedDirectory,
        PackageIdentifier identifier,
        List<ValidationFinding> findings)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParsedManifestDocument document in package.Documents)
        {
            string path = document.Document.RepositoryPath;
            ValidateRepositoryPath(path, findings);
            if (!seenPaths.Add(path))
            {
                findings.Add(Error("VLD2201", "The package set contains a duplicate repository path.", path));
            }

            string expectedFileName = document.Manifest switch
            {
                InstallerManifest => ManifestPaths.GetInstallerFileName(identifier),
                VersionManifest => ManifestPaths.GetVersionFileName(identifier),
                LocaleManifest locale when locale.PackageLocale is not null
                    => ManifestPaths.GetLocaleFileName(identifier, locale.PackageLocale),
                _ => string.Empty,
            };
            if (expectedFileName.Length == 0)
            {
                continue;
            }

            string expectedPath = $"{expectedDirectory}/{expectedFileName}";
            if (!string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                findings.Add(Error(
                    "VLD2202",
                    $"Manifest repository path must be exactly '{expectedPath}'.",
                    path));
            }
        }
    }

    private static void ValidateRepositoryPath(string path, List<ValidationFinding> findings)
    {
        if (path.Length == 0
            || path[0] == '/'
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            findings.Add(Error(
                "VLD2203",
                "Repository paths must be relative canonical Git paths using forward slashes.",
                path));
        }
    }

    private static void ValidateDiff(
        IReadOnlyList<RepositoryFileChange> changes,
        ParsedPackage package,
        string expectedDirectory,
        List<ValidationFinding> findings)
    {
        if (changes.Count == 0)
        {
            findings.Add(Error("VLD4001", "The repository diff must contain at least one changed file."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string prefix = $"{expectedDirectory}/";
        foreach (RepositoryFileChange change in changes.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            string path = change.RepositoryPath;
            ValidateRepositoryPath(path, findings);
            if (!seen.Add(path))
            {
                findings.Add(Error("VLD4002", "The repository diff lists the same path more than once.", path));
            }

            if (!path.StartsWith(prefix, StringComparison.Ordinal)
                || path[prefix.Length..].Contains('/', StringComparison.Ordinal))
            {
                findings.Add(Error(
                    "VLD4003",
                    $"Repository changes must target only package version directory '{expectedDirectory}'.",
                    path));
            }
        }

        HashSet<string> documentPaths =
        [
            .. package.Documents.Select(static item => item.Document.RepositoryPath),
        ];
        foreach (RepositoryFileChange change in changes)
        {
            bool documentExists = documentPaths.Contains(change.RepositoryPath);
            if (change.Kind == RepositoryChangeKind.Deleted && documentExists)
            {
                findings.Add(Error(
                    "VLD4004",
                    "A deleted diff path is still present in the post-change manifest set.",
                    change.RepositoryPath));
            }
            else if (change.Kind != RepositoryChangeKind.Deleted && !documentExists)
            {
                findings.Add(Error(
                    "VLD4005",
                    "A non-deleted diff path is not part of the complete manifest set.",
                    change.RepositoryPath));
            }
        }
    }

    private static void ValidateInstallers(
        InstallerManifest manifest,
        List<ValidationFinding> findings)
    {
        if (manifest.Installers is not { Count: > 0 } installers)
        {
            return;
        }

        var keys = new Dictionary<EffectiveInstallerKey, int>();
        var urlSemantics = new Dictionary<string, (InstallerSemantics Semantics, int Index)>(StringComparer.Ordinal);

        for (int i = 0; i < installers.Count; i++)
        {
            Installer installer = installers[i];
            var key = new EffectiveInstallerKey(
                installer.Architecture,
                installer.InstallerType ?? manifest.InstallerType,
                installer.Scope ?? manifest.Scope,
                installer.InstallerLocale ?? manifest.InstallerLocale);
            if (keys.TryGetValue(key, out int priorIndex))
            {
                findings.Add(Error(
                    "VLD3001",
                    $"Installer has the same effective architecture/type/scope/locale key as Installers[{priorIndex}].",
                    $"Installers[{i}]"));
            }
            else
            {
                keys.Add(key, i);
            }

            ValidateUrlSemantics(manifest, installer, i, urlSemantics, findings);
            ValidateNestedInstaller(manifest, installer, i, findings);
        }
    }

    private static void ValidateUrlSemantics(
        InstallerManifest manifest,
        Installer installer,
        int index,
        Dictionary<string, (InstallerSemantics Semantics, int Index)> urlSemantics,
        List<ValidationFinding> findings)
    {
        if (!Uri.TryCreate(installer.InstallerUrl, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        InstallerSwitches? switches = installer.InstallerSwitches ?? manifest.InstallerSwitches;
        var semantics = new InstallerSemantics(
            installer.InstallerType ?? manifest.InstallerType,
            installer.Scope ?? manifest.Scope,
            switches?.Silent,
            switches?.SilentWithProgress,
            switches?.Interactive,
            switches?.InstallLocation,
            switches?.Log,
            switches?.Upgrade,
            switches?.Custom,
            switches?.Repair);
        string url = uri.AbsoluteUri;
        if (urlSemantics.TryGetValue(url, out (InstallerSemantics Semantics, int Index) previous)
            && previous.Semantics != semantics)
        {
            findings.Add(Error(
                "VLD3002",
                $"The same installer URL has incompatible effective type, scope, or switch semantics as Installers[{previous.Index}].",
                $"Installers[{index}].InstallerUrl"));
        }
        else
        {
            urlSemantics.TryAdd(url, (semantics, index));
        }
    }

    private static void ValidateNestedInstaller(
        InstallerManifest manifest,
        Installer installer,
        int installerIndex,
        List<ValidationFinding> findings)
    {
        InstallerType? type = installer.InstallerType ?? manifest.InstallerType;
        InstallerType? nestedType = installer.NestedInstallerType ?? manifest.NestedInstallerType;
        List<NestedInstallerFile>? nestedFiles = installer.NestedInstallerFiles ?? manifest.NestedInstallerFiles;
        string path = $"Installers[{installerIndex}]";

        if (nestedType is not null || nestedFiles is { Count: > 0 })
        {
            if (type != InstallerType.Zip)
            {
                findings.Add(Error(
                    "VLD3003",
                    "Nested installer metadata is valid only for an effective zip installer.",
                    path));
            }

            if (nestedType is null || nestedFiles is not { Count: > 0 })
            {
                findings.Add(Error(
                    "VLD3004",
                    "NestedInstallerType and a non-empty NestedInstallerFiles list must be specified together.",
                    path));
                return;
            }
        }
        else
        {
            return;
        }

        var nestedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nestedFiles.Count; i++)
        {
            NestedInstallerFile nested = nestedFiles[i];
            string nestedPath = $"{path}.NestedInstallerFiles[{i}]";
            if (!IsSafeRelativeArchivePath(nested.RelativeFilePath))
            {
                findings.Add(Error(
                    "VLD3005",
                    "RelativeFilePath must be a non-rooted archive path without empty, '.' or '..' segments.",
                    $"{nestedPath}.RelativeFilePath"));
            }
            else if (!nestedPaths.Add(NormalizeArchivePath(nested.RelativeFilePath!)))
            {
                findings.Add(Error(
                    "VLD3006",
                    "Nested installer paths must be unique across the package set.",
                    $"{nestedPath}.RelativeFilePath"));
            }

            if (nested.PortableCommandAlias is not { } alias)
            {
                if (nestedType == InstallerType.Portable)
                {
                    findings.Add(Warning(
                        "VLD3007",
                        "Portable nested installers should declare a PortableCommandAlias.",
                        nestedPath));
                }

                continue;
            }

            if (nestedType != InstallerType.Portable)
            {
                findings.Add(Error(
                    "VLD3008",
                    "PortableCommandAlias is valid only for a nested portable installer.",
                    $"{nestedPath}.PortableCommandAlias"));
            }

            if (!IsSafeAlias(alias))
            {
                findings.Add(Error(
                    "VLD3009",
                    "PortableCommandAlias must be a single non-whitespace command name without path or drive separators.",
                    $"{nestedPath}.PortableCommandAlias"));
            }
            else if (!aliases.Add(alias))
            {
                findings.Add(Error(
                    "VLD3010",
                    "Portable command aliases must be unique across the package set.",
                    $"{nestedPath}.PortableCommandAlias"));
            }
        }
    }

    private static bool IsSafeRelativeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] is '/' or '\\'
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            return false;
        }

        return !path.Split(['/', '\\']).Any(static segment =>
            segment is "" or "." or ".." || !IsSafeWindowsPathSegment(segment));
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

    private static bool IsSafeWindowsPathSegment(string segment)
    {
        if (segment[^1] is '.' or ' '
            || segment.Any(static character =>
                char.IsControl(character)
                || character is '<' or '>' or '"' or ':' or '|' or '?' or '*'))
        {
            return false;
        }

        string deviceName = segment.Split('.')[0];
        return !deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !(deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && deviceName[3] is >= '1' and <= '9');
    }

    private static bool IsSafeAlias(string alias)
        => !string.IsNullOrWhiteSpace(alias)
            && !alias.Any(static value =>
                char.IsWhiteSpace(value)
                || value is '/' or '\\')
            && IsSafeWindowsPathSegment(alias);

    private static void ValidateNestedArchiveContents(
        InstallerManifest manifest,
        IReadOnlyList<InstallerArtifact> artifacts,
        List<ValidationFinding> findings)
    {
        if (manifest.Installers is not { Count: > 0 } installers)
        {
            return;
        }

        Dictionary<string, InstallerArtifact> artifactsByUrl = artifacts
            .GroupBy(static artifact => artifact.InstallerUrl, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var entriesByFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (int installerIndex = 0; installerIndex < installers.Count; installerIndex++)
        {
            Installer installer = installers[installerIndex];
            InstallerType? type = installer.InstallerType ?? manifest.InstallerType;
            List<NestedInstallerFile>? nestedFiles =
                installer.NestedInstallerFiles ?? manifest.NestedInstallerFiles;
            if (type != InstallerType.Zip
                || nestedFiles is not { Count: > 0 }
                || installer.InstallerUrl is not { } installerUrl
                || !artifactsByUrl.TryGetValue(installerUrl, out InstallerArtifact? artifact))
            {
                continue;
            }

            if (!entriesByFile.TryGetValue(artifact.Download.FilePath, out HashSet<string>? entries))
            {
                entries = ReadArchiveEntries(
                    artifact.Download,
                    installerIndex,
                    findings);
                if (entries is null)
                {
                    continue;
                }

                entriesByFile.Add(artifact.Download.FilePath, entries);
            }

            for (int nestedIndex = 0; nestedIndex < nestedFiles.Count; nestedIndex++)
            {
                string? relativePath = nestedFiles[nestedIndex].RelativeFilePath;
                if (!IsSafeRelativeArchivePath(relativePath))
                {
                    continue;
                }

                string normalized = relativePath!.Replace('\\', '/');
                if (!entries.Contains(normalized))
                {
                    findings.Add(Error(
                        "VLD3011",
                        $"Nested installer path '{relativePath}' does not exist in the downloaded archive with exact casing.",
                        $"Installers[{installerIndex}].NestedInstallerFiles[{nestedIndex}].RelativeFilePath"));
                }
            }
        }
    }

    private static HashSet<string>? ReadArchiveEntries(
        DownloadResult download,
        int installerIndex,
        List<ValidationFinding> findings)
    {
        try
        {
            using FileStream stream = File.Open(
                download.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length != download.SizeInBytes)
            {
                throw new InvalidDataException(
                    $"Downloaded archive size changed from {download.SizeInBytes} to {stream.Length} bytes.");
            }

            Sha256Hash actualHash = Sha256Hash.FromHashBytes(SHA256.HashData(stream));
            if (actualHash != download.Sha256)
            {
                throw new InvalidDataException(
                    $"Downloaded archive SHA-256 changed from '{download.Sha256}' to '{actualHash}'.");
            }

            stream.Position = 0;
            ValidateZipDirectory(stream);
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new InvalidDataException(
                    $"Archive contains {archive.Entries.Count} entries; the validation limit is {MaxArchiveEntries}.");
            }

            long expandedBytes = 0;
            var entries = new HashSet<string>(StringComparer.Ordinal);
            var windowsPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.Length > MaxArchivePathLength)
                {
                    throw new InvalidDataException(
                        $"Archive entry path exceeds {MaxArchivePathLength} characters.");
                }

                if (entry.Length < 0 || entry.Length > MaxArchiveEntryBytes)
                {
                    throw new InvalidDataException(
                        $"Archive entry '{entry.FullName}' declares {entry.Length} bytes; "
                        + $"the per-entry limit is {MaxArchiveEntryBytes}.");
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaxExpandedArchiveBytes)
                {
                    throw new InvalidDataException(
                        $"Archive expands beyond the {MaxExpandedArchiveBytes}-byte validation limit.");
                }

                string normalizedPath = NormalizeArchivePath(entry.FullName);
                bool isDirectory = normalizedPath.EndsWith('/');
                string pathForValidation = isDirectory
                    ? normalizedPath.TrimEnd('/')
                    : normalizedPath;
                if (!IsSafeRelativeArchivePath(pathForValidation))
                {
                    throw new InvalidDataException(
                        $"Archive entry '{entry.FullName}' is not a safe Windows-relative path.");
                }

                if (windowsPaths.TryGetValue(pathForValidation, out string? existingPath))
                {
                    throw new InvalidDataException(
                        $"Archive paths '{existingPath}' and '{normalizedPath}' collide on Windows.");
                }

                windowsPaths.Add(pathForValidation, normalizedPath);
                if (isDirectory)
                {
                    continue;
                }

                if (!entries.Add(normalizedPath))
                {
                    throw new InvalidDataException(
                        $"Archive contains duplicate canonical path '{normalizedPath}'.");
                }
            }

            return entries;
        }
        catch (InvalidDataException exception)
        {
            findings.Add(ArchiveError(exception.Message, installerIndex, download.FilePath));
        }
        catch (IOException exception)
        {
            findings.Add(ArchiveError(exception.Message, installerIndex, download.FilePath));
        }
        catch (UnauthorizedAccessException exception)
        {
            findings.Add(ArchiveError(exception.Message, installerIndex, download.FilePath));
        }
        catch (OverflowException exception)
        {
            findings.Add(ArchiveError(
                $"Archive declares an overflowing expanded size: {exception.Message}",
                installerIndex,
                download.FilePath));
        }

        return null;
    }

    private static void ValidateZipDirectory(FileStream stream)
    {
        const int minimumRecordLength = 22;
        const int maximumCommentLength = ushort.MaxValue;
        int tailLength = checked((int)Math.Min(
            stream.Length,
            minimumRecordLength + maximumCommentLength));
        if (tailLength < minimumRecordLength)
        {
            throw new InvalidDataException("Archive is too short to contain a ZIP central directory.");
        }

        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        int recordIndex = FindEndOfCentralDirectory(tail);
        if (recordIndex < 0)
        {
            throw new InvalidDataException("Archive has no valid ZIP end-of-central-directory record.");
        }

        ReadOnlySpan<byte> record = tail.AsSpan(recordIndex);
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            throw new InvalidDataException("Multi-disk ZIP archives are not supported.");
        }

        ulong resolvedEntries = totalEntries;
        ulong resolvedSize = directorySize;
        ulong resolvedOffset = directoryOffset;
        if (totalEntries == ushort.MaxValue
            || directorySize == uint.MaxValue
            || directoryOffset == uint.MaxValue)
        {
            long endRecordOffset = stream.Length - tailLength + recordIndex;
            (resolvedEntries, resolvedSize, resolvedOffset) =
                ReadZip64DirectoryInfo(stream, endRecordOffset);
        }

        if (resolvedEntries > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"Archive declares {resolvedEntries} entries; the validation limit is {MaxArchiveEntries}.");
        }

        if (resolvedSize > MaxCentralDirectoryBytes)
        {
            throw new InvalidDataException(
                $"Archive central directory declares {resolvedSize} bytes; "
                + $"the validation limit is {MaxCentralDirectoryBytes}.");
        }

        ulong fileLength = checked((ulong)stream.Length);
        if (resolvedOffset > fileLength
            || resolvedSize > fileLength - resolvedOffset)
        {
            throw new InvalidDataException("Archive central directory extends beyond the file.");
        }
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> tail)
    {
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[index..]) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail[(index + 20)..]);
            if (index + 22 + commentLength == tail.Length)
            {
                return index;
            }
        }

        return -1;
    }

    private static (ulong Entries, ulong Size, ulong Offset) ReadZip64DirectoryInfo(
        FileStream stream,
        long endRecordOffset)
    {
        if (endRecordOffset < 20)
        {
            throw new InvalidDataException("ZIP64 archive has no locator record.");
        }

        Span<byte> locator = stackalloc byte[20];
        stream.Position = endRecordOffset - locator.Length;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw new InvalidDataException("ZIP64 locator is invalid or describes multiple disks.");
        }

        ulong recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (recordOffset > checked((ulong)Math.Max(0, stream.Length - 56)))
        {
            throw new InvalidDataException("ZIP64 end-of-central-directory record is outside the file.");
        }

        Span<byte> record = stackalloc byte[56];
        stream.Position = checked((long)recordOffset);
        stream.ReadExactly(record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature
            || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) != 0)
        {
            throw new InvalidDataException("ZIP64 end-of-central-directory record is invalid.");
        }

        ulong entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
        ulong totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        if (entriesOnDisk != totalEntries)
        {
            throw new InvalidDataException("Multi-disk ZIP64 archives are not supported.");
        }

        return (
            totalEntries,
            BinaryPrimitives.ReadUInt64LittleEndian(record[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[48..]));
    }

    private static ValidationFinding ArchiveError(
        string message,
        int installerIndex,
        string filePath)
        => Error(
            "VLD3012",
            $"Downloaded nested-installer archive could not be inspected: {message}",
            $"Installers[{installerIndex}]:{filePath}");

    private static void ValidateArpOverlap(
        PackageManifests manifests,
        IReadOnlyList<ExistingVersionSnapshot> existingVersions,
        List<ValidationFinding> findings)
    {
        string? packageVersion = manifests.Version.PackageVersion?.Value;
        HashSet<string> currentDisplayVersions = GetEffectiveDisplayVersions(manifests.Installer);
        VersionRange? currentRange = CreateVersionRange(currentDisplayVersions);
        foreach (ExistingVersionSnapshot existing in existingVersions
                     .OrderBy(static item => item.PackageVersion, StringComparer.Ordinal))
        {
            if (string.Equals(existing.PackageVersion, packageVersion, StringComparison.Ordinal))
            {
                continue;
            }

            VersionRange? existingRange = CreateVersionRange(existing.DisplayVersions);
            if (currentRange is not null
                && existingRange is not null
                && RangesOverlap(currentRange, existingRange))
            {
                findings.Add(Error(
                    "VLD3101",
                    $"ARP DisplayVersion range '{currentRange.Minimum.Raw}'-'{currentRange.Maximum.Raw}' "
                    + $"overlaps range '{existingRange.Minimum.Raw}'-'{existingRange.Maximum.Raw}' "
                    + $"from package version '{existing.PackageVersion}'.",
                    "AppsAndFeaturesEntries.DisplayVersion"));
            }
        }
    }

    private static VersionRange? CreateVersionRange(IEnumerable<string> values)
    {
        ComparableVersion[] versions =
        [
            .. values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Select(static value => new ComparableVersion(
                    value,
                    PackageVersion.TryCreate(value, out PackageVersion? parsed) ? parsed : null)),
        ];
        if (versions.Length == 0)
        {
            return null;
        }

        ComparableVersion minimum = versions[0];
        ComparableVersion maximum = versions[0];
        foreach (ComparableVersion version in versions.AsSpan(1))
        {
            if (CompareVersions(version, minimum) < 0)
            {
                minimum = version;
            }

            if (CompareVersions(version, maximum) > 0)
            {
                maximum = version;
            }
        }

        return new VersionRange(minimum, maximum);
    }

    private static bool RangesOverlap(VersionRange left, VersionRange right)
        => CompareVersions(left.Minimum, right.Maximum) <= 0
            && CompareVersions(right.Minimum, left.Maximum) <= 0;

    private static int CompareVersions(ComparableVersion left, ComparableVersion right)
    {
        if (left.Parsed is not null && right.Parsed is not null)
        {
            return left.Parsed.IsEquivalentTo(right.Parsed)
                ? 0
                : left.Parsed.CompareTo(right.Parsed);
        }

        return string.Compare(left.Raw, right.Raw, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetEffectiveDisplayVersions(InstallerManifest manifest)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.Installers is null)
        {
            return values;
        }

        foreach (Installer installer in manifest.Installers)
        {
            List<AppsAndFeaturesEntry>? entries =
                installer.AppsAndFeaturesEntries ?? manifest.AppsAndFeaturesEntries;
            if (entries is null)
            {
                continue;
            }

            foreach (AppsAndFeaturesEntry entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.DisplayVersion))
                {
                    values.Add(entry.DisplayVersion);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<UrlTarget> CollectUrls(PackageManifests manifests)
    {
        var urls = new Dictionary<string, UrlTargetKind>(StringComparer.Ordinal);
        if (manifests.Installer.Installers is { } installers)
        {
            foreach (Installer installer in installers)
            {
                AddUrl(urls, installer.InstallerUrl, UrlTargetKind.Installer);
                AddUrls(
                    urls,
                    (installer.ExpectedReturnCodes ?? manifests.Installer.ExpectedReturnCodes)
                        ?.Select(static code => code.ReturnResponseUrl));
            }
        }

        AddLocaleUrls(urls, manifests.DefaultLocale);
        foreach (LocaleManifest locale in manifests.Locales)
        {
            AddLocaleUrls(urls, locale);
        }

        return
        [
            .. urls.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new UrlTarget(item.Key, item.Value)),
        ];
    }

    private static void AddLocaleUrls(
        Dictionary<string, UrlTargetKind> urls,
        LocaleManifest locale)
    {
        AddUrls(
            urls,
            [
                locale.PublisherUrl,
                locale.PublisherSupportUrl,
                locale.PrivacyUrl,
                locale.PackageUrl,
                locale.LicenseUrl,
                locale.CopyrightUrl,
                locale.ReleaseNotesUrl,
                locale.PurchaseUrl,
            ]);
        AddUrls(urls, locale.Agreements?.Select(static item => item.AgreementUrl));
        AddUrls(urls, locale.Documentations?.Select(static item => item.DocumentUrl));
        AddUrls(urls, locale.Icons?.Select(static item => item.IconUrl));
    }

    private static void AddUrls(
        Dictionary<string, UrlTargetKind> urls,
        IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string? value in values)
        {
            AddUrl(urls, value, UrlTargetKind.Metadata);
        }
    }

    private static void AddUrl(
        Dictionary<string, UrlTargetKind> urls,
        string? value,
        UrlTargetKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (urls.TryGetValue(value, out UrlTargetKind existing)
            && existing == UrlTargetKind.Installer)
        {
            return;
        }

        urls[value] = kind;
    }

    private static IReadOnlyList<ExpectedInstallerHash> CollectInstallerHashes(
        InstallerManifest manifest)
    {
        if (manifest.Installers is null)
        {
            return [];
        }

        return
        [
            .. manifest.Installers
                .Where(static installer =>
                    installer.InstallerUrl is not null
                    && installer.InstallerSha256 is not null)
                .Select(static installer => new ExpectedInstallerHash(
                    installer.InstallerUrl!,
                    installer.InstallerSha256!))
                .Distinct()
                .OrderBy(static item => item.Url, StringComparer.Ordinal),
        ];
    }

    private static ValidationFinding Error(string code, string message, string? path = null)
        => new(code, ValidationSeverity.Error, message, path);

    private static ValidationFinding Warning(string code, string message, string? path = null)
        => new(code, ValidationSeverity.Warning, message, path);

    private sealed record EffectiveInstallerKey(
        Architecture? Architecture,
        InstallerType? InstallerType,
        Scope? Scope,
        LanguageTag? Locale);

    private sealed record InstallerSemantics(
        InstallerType? InstallerType,
        Scope? Scope,
        string? Silent,
        string? SilentWithProgress,
        string? Interactive,
        string? InstallLocation,
        string? Log,
        string? Upgrade,
        string? Custom,
        string? Repair);

    private sealed record ComparableVersion(string Raw, PackageVersion? Parsed);

    private sealed record VersionRange(ComparableVersion Minimum, ComparableVersion Maximum);
}

internal sealed record SemanticValidationResult(
    IReadOnlyList<UrlTarget> Urls,
    IReadOnlyList<ExpectedInstallerHash> InstallerHashes);

internal sealed record UrlTarget(string Url, UrlTargetKind Kind);

internal enum UrlTargetKind
{
    Metadata,
    Installer,
}

internal sealed record ExpectedInstallerHash(string Url, Sha256Hash Sha256);
