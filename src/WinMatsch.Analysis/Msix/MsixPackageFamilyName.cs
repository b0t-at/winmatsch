using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinMatsch.Analysis.Msix;

/// <summary>
/// Computes MSIX/AppX package family names: <c>{IdentityName}_{PublisherId}</c>, where the
/// publisher id is derived from the certificate subject string in the package identity.
/// </summary>
internal static class MsixPackageFamilyName
{
    // Crockford base32 with the lowercase alphabet Windows uses (no i, l, o, u).
    private const string CrockfordAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Builds the package family name from the identity name and publisher subject.</summary>
    public static string Create(string identityName, string publisher)
        => $"{identityName}_{ComputePublisherId(publisher)}";

    /// <summary>
    /// Derives the 13-character publisher id: the first 8 bytes of SHA-256 over the publisher
    /// string encoded as UTF-16LE (no BOM), read as a big-endian 64-bit value, padded with a
    /// trailing zero bit to 65 bits and emitted as 13 Crockford base32 characters.
    /// </summary>
    public static string ComputePublisherId(string publisher)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.Unicode.GetBytes(publisher), hash);
        ulong bits = BinaryPrimitives.ReadUInt64BigEndian(hash);

        Span<char> characters = stackalloc char[13];
        for (int i = 0; i < 12; i++)
        {
            characters[i] = CrockfordAlphabet[(int)((bits >> (59 - (5 * i))) & 0x1F)];
        }

        // The last character covers the final four data bits plus the zero pad bit.
        characters[12] = CrockfordAlphabet[(int)((bits << 1) & 0x1F)];
        return new string(characters);
    }
}
