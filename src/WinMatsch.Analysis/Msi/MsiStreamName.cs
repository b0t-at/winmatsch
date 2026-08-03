namespace WinMatsch.Analysis.Msi;

/// <summary>
/// Decodes the compressed CFB stream names used inside MSI databases. Each UTF-16 code unit
/// in the range U+3800–U+483F encodes one or two characters of a 64-character alphabet;
/// table streams additionally carry the prefix U+4840. All other characters pass through
/// unchanged (the <c>\x05SummaryInformation</c> stream, for example, is not encoded).
/// </summary>
internal static class MsiStreamName
{
    /// <summary>The alphabet used by the two-characters-per-code-unit encoding; index 62 is '.', 63 is '_'.</summary>
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    /// <summary>Marks a stream as a database table when it is the first character of the name.</summary>
    private const char TablePrefix = '\u4840';

    /// <summary>Decodes an encoded CFB stream name into its logical MSI name.</summary>
    /// <param name="encoded">The raw stream name as stored in the compound file.</param>
    /// <param name="isTable">Whether the stream is a database table (had the table prefix).</param>
    public static string Decode(string encoded, out bool isTable)
    {
        var builder = new System.Text.StringBuilder(encoded.Length * 2);
        isTable = false;

        int start = 0;
        if (encoded.Length > 0 && encoded[0] == TablePrefix)
        {
            isTable = true;
            start = 1;
        }

        for (int i = start; i < encoded.Length; i++)
        {
            int value = encoded[i];
            if (value is >= 0x3800 and < 0x4800)
            {
                value -= 0x3800;
                builder.Append(Alphabet[value & 0x3F]);
                builder.Append(Alphabet[value >> 6]);
            }
            else if (value is >= 0x4800 and < 0x4840)
            {
                builder.Append(Alphabet[value - 0x4800]);
            }
            else
            {
                builder.Append(encoded[i]);
            }
        }

        return builder.ToString();
    }
}
