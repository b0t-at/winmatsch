namespace WinMatsch.Rules;

/// <summary>
/// The stable identifiers of all built-in rules. Ids are never reused or renumbered:
/// <c>WM00xx</c> are normalization rules, <c>WM01xx</c> validation rules and
/// <c>WM02xx</c> quirk rules.
/// </summary>
public static class RuleIds
{
    public const string HoistCommonInstallerFields = "WM0001";
    public const string PushDownRootFields = "WM0002";
    public const string DedupeArpVsDefaultLocale = "WM0003";
    public const string ScrubEmptyStrings = "WM0004";
    public const string RemoveDuplicateInstallers = "WM0005";
    public const string NormalizeProductCodes = "WM0006";
    public const string PreserveOnUpdate = "WM0007";

    public const string DisplayVersionConsistency = "WM0101";
    public const string DuplicateInstallerEntries = "WM0102";
    public const string InstallerTypeConsistency = "WM0103";

    public const string ApplyPackageQuirks = "WM0201";
    public const string ApplyOverridePackFields = "WM0202";
}
