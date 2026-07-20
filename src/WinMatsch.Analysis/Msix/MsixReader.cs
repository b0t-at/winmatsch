using System.IO.Compression;
using System.Security.Cryptography;
using WinMatsch.Core;

namespace WinMatsch.Analysis.Msix;

/// <summary>Helpers shared by the MSIX package and bundle analyzers.</summary>
internal static class MsixReader
{
    /// <summary>The signature file makeappx places at the root of signed packages and bundles.</summary>
    private const string SignatureEntryName = "AppxSignature.p7x";

    /// <summary>Reads a zip entry fully into memory.</summary>
    public static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream(checked((int)entry.Length));
        using (Stream entryStream = entry.Open())
        {
            entryStream.CopyTo(buffer);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The SHA-256 of the package's <c>AppxSignature.p7x</c> entry (the manifest's
    /// <c>SignatureSha256</c> value), or null when the package is unsigned.
    /// </summary>
    public static Sha256Hash? ComputeSignatureHash(ZipArchive archive)
    {
        ZipArchiveEntry? signature = archive.GetEntry(SignatureEntryName);
        return signature is null ? null : Sha256Hash.FromHashBytes(SHA256.HashData(ReadEntryBytes(signature)));
    }

    /// <summary>
    /// Maps a manifest architecture token to the manifest enum. An absent attribute means
    /// architecture-neutral, the schema default.
    /// </summary>
    /// <exception cref="InvalidDataException">The token is not a known architecture.</exception>
    public static Architecture ParseArchitecture(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.Neutral;
        }

        if (value.Equals("x86", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.X86;
        }

        if (value.Equals("x64", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.X64;
        }

        if (value.Equals("arm", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.Arm;
        }

        if (value.Equals("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return Architecture.Arm64;
        }

        throw new InvalidDataException($"The package manifest declares an unknown processor architecture '{value}'.");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Returns the attribute value, or null when the attribute is absent or empty.</summary>
    public static string? GetAttribute(System.Xml.XmlReader reader, string name) => NullIfEmpty(reader.GetAttribute(name));
}
