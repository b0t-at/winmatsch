using WinMatsch.Core;

namespace WinMatsch.Analysis;

/// <summary>
/// Maps Windows LCIDs to BCP 47 language tags. InvariantGlobalization is enabled, so
/// <c>CultureInfo</c> cannot resolve LCIDs and a hand-written map of the LCIDs common in real
/// packages is used instead. Unknown LCIDs (and 0, "language neutral") map to no locale.
/// Shared by the analyzers that read an LCID from their format (MSI ProductLanguage, NSIS
/// language tables).
/// </summary>
internal static class Lcid
{
    private static readonly Dictionary<int, string> _lcidToLanguageTag = new()
    {
        [1025] = "ar-SA",
        [1028] = "zh-TW",
        [1029] = "cs-CZ",
        [1030] = "da-DK",
        [1031] = "de-DE",
        [1032] = "el-GR",
        [1033] = "en-US",
        [1034] = "es-ES",
        [1035] = "fi-FI",
        [1036] = "fr-FR",
        [1037] = "he-IL",
        [1038] = "hu-HU",
        [1040] = "it-IT",
        [1041] = "ja-JP",
        [1042] = "ko-KR",
        [1043] = "nl-NL",
        [1044] = "nb-NO",
        [1045] = "pl-PL",
        [1046] = "pt-BR",
        [1049] = "ru-RU",
        [1053] = "sv-SE",
        [1054] = "th-TH",
        [1055] = "tr-TR",
        [1057] = "id-ID",
        [1058] = "uk-UA",
        [1066] = "vi-VN",
        [1081] = "hi-IN",
        [2052] = "zh-CN",
        [2070] = "pt-PT",
        [3082] = "es-ES",
    };

    /// <summary>The language tag for the LCID, or null when the LCID is unknown or neutral.</summary>
    public static LanguageTag? ToLanguageTag(int lcid)
        => _lcidToLanguageTag.TryGetValue(lcid, out string? tag) ? new LanguageTag(tag) : null;
}
