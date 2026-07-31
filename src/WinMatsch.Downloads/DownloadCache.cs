using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinMatsch.Core;

namespace WinMatsch.Downloads;

/// <summary>
/// A bounded persistent installer cache with payload integrity verification, atomic file replacement,
/// expiry, inspection, and serialized access across threads, instances, and processes.
/// </summary>
public sealed class DownloadCache
{
    private const string MetadataSuffix = ".json";
    private const string PayloadSuffix = ".payload";
    private const string TempSuffix = ".tmp";
    private const string LockFileName = ".winmatsch-cache.lock";
    private const int CopyBufferSize = 81920;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly DownloadCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate;

    public DownloadCache(string directory, DownloadCacheOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _options = options ?? new DownloadCacheOptions();
        ValidateOptions(_options);
        _timeProvider = _options.TimeProvider;
        _gate = _cacheGates.GetOrAdd(_directory, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>The full cache directory path.</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// Restores a fresh, integrity-checked entry into <paramref name="destinationDirectory"/>.
    /// Returns null for a miss or expired entry and throws for corruption.
    /// </summary>
    public async Task<DownloadResult?> TryRestoreAsync(
        string url,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            string key = CreateKey(url);
            CacheMetadata? metadata = await ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            ValidateLeafName(metadata.FileName, "cached file name");
            string payloadPath = GetPayloadPath(metadata.PayloadFileName);
            if (!File.Exists(payloadPath))
            {
                throw Corruption(payloadPath, "The cache metadata exists but its payload is missing.");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (metadata.ExpiresAt <= now)
            {
                DeleteEntry(key);
                return null;
            }

            try
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw DestinationFailure(
                    destinationDirectory,
                    $"Failed to create the cache restoration destination '{destinationDirectory}'.",
                    exception);
            }

            string preferredPath = Path.Combine(destinationDirectory, metadata.FileName);
            string tempPath = preferredPath + TempSuffix + "." + Guid.NewGuid().ToString("N");
            DownloadContentIdentity actual;
            string destinationPath;
            try
            {
                actual = await RestorePayloadAsync(payloadPath, tempPath, cancellationToken).ConfigureAwait(false);
                EnsureIdentity(metadata, actual, payloadPath);
                destinationPath = await DownloadDestination.PublishAsync(
                    tempPath,
                    preferredPath,
                    actual,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            metadata.LastAccessedAt = now;
            await WriteMetadataAsync(key, metadata, cancellationToken).ConfigureAwait(false);
            return ToResult(metadata, destinationPath, isFromCache: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores an installer and its integrity/HTTP metadata, then enforces cache bounds.</summary>
    public async Task StoreAsync(DownloadResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_directory);
            string key = CreateKey(result.InitialUrl);
            if (!result.MayBeStored)
            {
                DeleteEntry(key);
                return;
            }

            ValidateLeafName(result.FileName, "download file name");
            CacheMetadata? previousMetadata = null;
            try
            {
                previousMetadata = await ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadCacheCorruptionException)
            {
                DeleteEntry(key);
            }

            string payloadFileName = key + "." + Guid.NewGuid().ToString("N") + PayloadSuffix;
            string payloadPath = GetPayloadPath(payloadFileName);
            string tempPayloadPath = payloadPath + TempSuffix + "." + Guid.NewGuid().ToString("N");
            try
            {
                DownloadContentIdentity actual = await CopyAndHashAsync(result.FilePath, tempPayloadPath, cancellationToken).ConfigureAwait(false);
                if (actual != result.ContentIdentity)
                {
                    throw new DownloadContentChangedException(result.ContentIdentity, actual, result.FilePath);
                }

                File.Move(tempPayloadPath, payloadPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPayloadPath);
                throw;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset ttlExpiry = now + _options.TimeToLive;
            DateTimeOffset expiresAt = result.FreshUntil is { } freshUntil && freshUntil < ttlExpiry
                ? freshUntil
                : ttlExpiry;
            var metadata = CacheMetadata.FromResult(result, key, payloadFileName, now, expiresAt);
            await WriteMetadataAsync(key, metadata, cancellationToken).ConfigureAwait(false);
            if (previousMetadata is not null
                && !string.Equals(previousMetadata.PayloadFileName, payloadFileName, StringComparison.Ordinal))
            {
                File.Delete(GetPayloadPath(previousMetadata.PayloadFileName));
            }

            await EnforceBoundsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns all entries with current expiry and payload-integrity state.</summary>
    public async Task<IReadOnlyList<DownloadCacheEntryInfo>> InspectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var entries = new List<DownloadCacheEntryInfo>();
            foreach (string metadataPath in Directory.EnumerateFiles(_directory, "*" + MetadataSuffix).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = Path.GetFileNameWithoutExtension(metadataPath);
                try
                {
                    CacheMetadata? metadata = await ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);
                    if (metadata is null)
                    {
                        continue;
                    }

                    string payloadPath = GetPayloadPath(metadata.PayloadFileName);
                    DownloadCacheEntryState state = DownloadCacheEntryState.Stale;
                    if (metadata.ExpiresAt > _timeProvider.GetUtcNow())
                    {
                        if (!File.Exists(payloadPath))
                        {
                            state = DownloadCacheEntryState.Corrupt;
                        }
                        else
                        {
                            DownloadContentIdentity actual = await ComputeIdentityAsync(payloadPath, cancellationToken).ConfigureAwait(false);
                            state = actual == metadata.ContentIdentity
                                ? DownloadCacheEntryState.Fresh
                                : DownloadCacheEntryState.Corrupt;
                        }
                    }

                    entries.Add(new DownloadCacheEntryInfo
                    {
                        Url = metadata.InitialUrl,
                        CacheKey = key,
                        ContentIdentity = metadata.ContentIdentity,
                        CreatedAt = metadata.CreatedAt,
                        LastAccessedAt = metadata.LastAccessedAt,
                        ExpiresAt = metadata.ExpiresAt,
                        State = state,
                    });
                }
                catch (DownloadCacheCorruptionException)
                {
                    entries.Add(new DownloadCacheEntryInfo
                    {
                        Url = string.Empty,
                        CacheKey = key,
                        State = DownloadCacheEntryState.Corrupt,
                    });
                }
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears one URL entry, or every entry when <paramref name="url"/> is null.</summary>
    public async Task ClearAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream processLock = await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(_directory))
            {
                return;
            }

            if (url is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(url);
                DeleteEntry(CreateKey(url));
                return;
            }

            foreach (string path in Directory.EnumerateFiles(_directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetFileName(path), LockFileName, StringComparison.Ordinal))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnforceBoundsAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CacheMetadata>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*" + MetadataSuffix))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            try
            {
                CacheMetadata? metadata = await ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);
                if (metadata is not null)
                {
                    entries.Add(metadata);
                }
            }
            catch (DownloadCacheCorruptionException)
            {
                DeleteEntry(key);
            }
        }

        var referencedPayloads = entries.Select(static entry => entry.PayloadFileName).ToHashSet(StringComparer.Ordinal);
        foreach (string payloadPath in Directory.EnumerateFiles(_directory, "*" + PayloadSuffix))
        {
            if (!referencedPayloads.Contains(Path.GetFileName(payloadPath)))
            {
                File.Delete(payloadPath);
            }
        }

        long totalBytes = entries.Sum(static entry => entry.SizeInBytes);
        int remainingEntries = entries.Count;
        foreach (CacheMetadata entry in entries.OrderBy(static entry => entry.LastAccessedAt))
        {
            if (remainingEntries <= _options.MaxEntries && totalBytes <= _options.MaxBytes)
            {
                break;
            }

            DeleteEntry(entry.CacheKey);
            totalBytes -= entry.SizeInBytes;
            remainingEntries--;
        }
    }

    private async Task<CacheMetadata?> ReadMetadataAsync(string key, CancellationToken cancellationToken)
    {
        string metadataPath = GetMetadataPath(key);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            var metadata = new CacheMetadata
            {
                CacheKey = RequiredString(root, "cacheKey"),
                PayloadFileName = RequiredString(root, "payloadFileName"),
                InitialUrl = RequiredString(root, "initialUrl"),
                FinalUrl = RequiredString(root, "finalUrl"),
                FileName = RequiredString(root, "fileName"),
                Sha256 = RequiredString(root, "sha256"),
                SizeInBytes = root.GetProperty("sizeInBytes").GetInt64(),
                CreatedAt = RequiredDate(root, "createdAt"),
                LastAccessedAt = RequiredDate(root, "lastAccessedAt"),
                ExpiresAt = RequiredDate(root, "expiresAt"),
                RetrievedAt = RequiredDate(root, "retrievedAt"),
                ETag = OptionalString(root, "etag"),
                LastModified = OptionalDate(root, "lastModified"),
                ResponseDate = OptionalDate(root, "responseDate"),
                FreshUntil = OptionalDate(root, "freshUntil"),
                ContentType = OptionalString(root, "contentType"),
                MayBeStored = !root.TryGetProperty("mayBeStored", out JsonElement mayBeStored) || mayBeStored.GetBoolean(),
            };

            if (!string.Equals(metadata.CacheKey, key, StringComparison.Ordinal)
                || metadata.SizeInBytes < 0
                || !string.Equals(CreateKey(metadata.InitialUrl), key, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Cache metadata identity fields are inconsistent.");
            }

            ValidateLeafName(metadata.FileName, "cached file name");
            ValidatePayloadFileName(metadata.PayloadFileName, key);
            _ = metadata.ContentIdentity;
            return metadata;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadCacheCorruptionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or JsonException
            or KeyNotFoundException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw Corruption(metadataPath, "The cache metadata is malformed or unreadable.", exception);
        }
    }

    private async Task WriteMetadataAsync(string key, CacheMetadata metadata, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        string metadataPath = GetMetadataPath(key);
        string tempPath = metadataPath + TempSuffix + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("cacheKey", metadata.CacheKey);
                writer.WriteString("payloadFileName", metadata.PayloadFileName);
                writer.WriteString("initialUrl", metadata.InitialUrl);
                writer.WriteString("finalUrl", metadata.FinalUrl);
                writer.WriteString("fileName", metadata.FileName);
                writer.WriteString("sha256", metadata.Sha256);
                writer.WriteNumber("sizeInBytes", metadata.SizeInBytes);
                writer.WriteString("createdAt", metadata.CreatedAt);
                writer.WriteString("lastAccessedAt", metadata.LastAccessedAt);
                writer.WriteString("expiresAt", metadata.ExpiresAt);
                writer.WriteString("retrievedAt", metadata.RetrievedAt);
                WriteOptional(writer, "etag", metadata.ETag);
                WriteOptional(writer, "lastModified", metadata.LastModified);
                WriteOptional(writer, "responseDate", metadata.ResponseDate);
                WriteOptional(writer, "freshUntil", metadata.FreshUntil);
                WriteOptional(writer, "contentType", metadata.ContentType);
                writer.WriteBoolean("mayBeStored", metadata.MayBeStored);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, metadataPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task<DownloadContentIdentity> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            size += read;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new DownloadContentIdentity(Sha256Hash.FromHashBytes(hash.GetHashAndReset()), size);
    }

    private static async Task<DownloadContentIdentity> RestorePayloadAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Corruption(sourcePath, "The cached payload could not be opened for restoration.", exception);
        }

        await using (source.ConfigureAwait(false))
        {
            FileStream destination;
            try
            {
                destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw DestinationFailure(
                    destinationPath,
                    $"Failed to create the cache restoration file '{destinationPath}'.",
                    exception);
            }

            await using (destination.ConfigureAwait(false))
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[CopyBufferSize];
                long size = 0;
                while (true)
                {
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        throw Corruption(sourcePath, "The cached payload could not be read during restoration.", exception);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw DestinationFailure(
                            destinationPath,
                            $"Failed to write the cache restoration file '{destinationPath}'.",
                            exception);
                    }

                    size += read;
                }

                try
                {
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw DestinationFailure(
                        destinationPath,
                        $"Failed to flush the cache restoration file '{destinationPath}'.",
                        exception);
                }

                return new DownloadContentIdentity(Sha256Hash.FromHashBytes(hash.GetHashAndReset()), size);
            }
        }
    }

    private static async Task<DownloadContentIdentity> ComputeIdentityAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Sha256Hash hash = await Sha256Hash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
        return new DownloadContentIdentity(hash, stream.Length);
    }

    private static void EnsureIdentity(CacheMetadata metadata, DownloadContentIdentity actual, string payloadPath)
    {
        if (actual != metadata.ContentIdentity)
        {
            throw Corruption(payloadPath, "The cached payload does not match its recorded SHA-256 and size.");
        }
    }

    private static DownloadResult ToResult(CacheMetadata metadata, string filePath, bool isFromCache)
        => new()
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Sha256 = new Sha256Hash(metadata.Sha256),
            SizeInBytes = metadata.SizeInBytes,
            LastModified = metadata.LastModified,
            ETag = metadata.ETag,
            ResponseDate = metadata.ResponseDate,
            FreshUntil = metadata.FreshUntil,
            RetrievedAt = metadata.RetrievedAt,
            InitialUrl = metadata.InitialUrl,
            FinalUrl = metadata.FinalUrl,
            ContentType = metadata.ContentType,
            IsFromCache = isFromCache,
            MayBeStored = metadata.MayBeStored,
        };

    private static string CreateKey(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

    private string GetMetadataPath(string key) => Path.Combine(_directory, key + MetadataSuffix);

    private string GetPayloadPath(string payloadFileName) => Path.Combine(_directory, payloadFileName);

    private void DeleteEntry(string key)
    {
        File.Delete(GetMetadataPath(key));
        foreach (string payloadPath in Directory.EnumerateFiles(_directory, key + ".*" + PayloadSuffix))
        {
            File.Delete(payloadPath);
        }

        File.Delete(Path.Combine(_directory, key + PayloadSuffix));
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        string lockPath = Path.Combine(_directory, LockFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static DownloadCacheCorruptionException Corruption(string path, string message, Exception? innerException = null)
        => new(path, message, innerException);

    private static DownloadFileException DestinationFailure(string path, string message, Exception innerException)
        => new(path, message, innerException);

    private static void ValidateLeafName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            || value is "." or ".."
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"The {field} must be a safe leaf name.");
        }
    }

    private static void ValidatePayloadFileName(string value, string key)
    {
        ValidateLeafName(value, "cache payload file name");
        if (!value.StartsWith(key + ".", StringComparison.Ordinal) || !value.EndsWith(PayloadSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cache payload file name does not belong to its metadata key.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
        => root.GetProperty(name).GetString() ?? throw new InvalidDataException($"'{name}' cannot be null.");

    private static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static DateTimeOffset RequiredDate(JsonElement root, string name)
        => root.GetProperty(name).GetDateTimeOffset();

    private static DateTimeOffset? OptionalDate(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDateTimeOffset()
            : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Value);
        }
    }

    private static void ValidateOptions(DownloadCacheOptions options)
    {
        if (options.TimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache TTL must be positive.");
        }

        if (options.MaxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache entry capacity must be at least one.");
        }

        if (options.MaxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache byte capacity must be at least one.");
        }

        ArgumentNullException.ThrowIfNull(options.TimeProvider);
    }

    private sealed class CacheMetadata
    {
        public required string CacheKey { get; init; }

        public required string PayloadFileName { get; init; }

        public required string InitialUrl { get; init; }

        public required string FinalUrl { get; init; }

        public required string FileName { get; init; }

        public required string Sha256 { get; init; }

        public required long SizeInBytes { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        public required DateTimeOffset RetrievedAt { get; init; }

        public required DateTimeOffset LastAccessedAt { get; set; }

        public string? ETag { get; init; }

        public DateTimeOffset? LastModified { get; init; }

        public DateTimeOffset? ResponseDate { get; init; }

        public DateTimeOffset? FreshUntil { get; init; }

        public string? ContentType { get; init; }

        public bool MayBeStored { get; init; } = true;

        public DownloadContentIdentity ContentIdentity => new(new Sha256Hash(Sha256), SizeInBytes);

        public static CacheMetadata FromResult(
            DownloadResult result,
            string cacheKey,
            string payloadFileName,
            DateTimeOffset now,
            DateTimeOffset expiresAt)
            => new()
            {
                CacheKey = cacheKey,
                PayloadFileName = payloadFileName,
                InitialUrl = result.InitialUrl,
                FinalUrl = result.FinalUrl,
                FileName = result.FileName,
                Sha256 = result.Sha256.Normalized,
                SizeInBytes = result.SizeInBytes,
                CreatedAt = now,
                LastAccessedAt = now,
                ExpiresAt = expiresAt,
                RetrievedAt = result.RetrievedAt,
                ETag = result.ETag,
                LastModified = result.LastModified,
                ResponseDate = result.ResponseDate,
                FreshUntil = result.FreshUntil,
                ContentType = result.ContentType,
                MayBeStored = result.MayBeStored,
            };
    }
}
