using BinMatch.Core;

namespace BinMatch.Downloads;

/// <summary>A stable installer identity composed of its SHA-256 and byte length.</summary>
public sealed record DownloadContentIdentity(Sha256Hash Sha256, long SizeInBytes);
