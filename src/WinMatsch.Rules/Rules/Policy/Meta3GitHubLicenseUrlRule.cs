using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-3: normalizes GitHub license/copyright URLs to the stable
/// <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/blob/HEAD/&lt;file&gt;</c> form. Deliberately
/// conservative: only full-40-hex commit-pinned <c>blob</c> links (which rot when history is
/// rewritten or the file moves; short hex refs could be branch names and are left alone) and
/// <c>raw.githubusercontent.com</c> links (which render as plain text) are rewritten.
/// Branch-named blob links are left alone — renaming a default branch is the publisher's
/// decision, not this rule's.
/// </summary>
public sealed partial class Meta3GitHubLicenseUrlRule : IRule
{
    public string Id => RuleCatalogueIds.Meta3;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Normalizes GitHub license/copyright links to stable blob/HEAD URLs.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach ((LocaleManifest locale, string documentName) in PolicyValues.EnumerateLocales(context.Manifests))
        {
            string manifestPath = PolicyValues.GetLocaleManifestPath(context.Manifests, locale);
            locale.LicenseUrl = Normalize(context, locale.LicenseUrl, documentName, manifestPath, nameof(locale.LicenseUrl));
            locale.CopyrightUrl = Normalize(context, locale.CopyrightUrl, documentName, manifestPath, nameof(locale.CopyrightUrl));
        }
    }

    private string? Normalize(ManifestContext context, string? url, string documentName, string manifestPath, string fieldName)
    {
        if (url is null)
        {
            return null;
        }

        string? normalized = TryNormalize(url);
        if (normalized is null || string.Equals(normalized, url, StringComparison.Ordinal))
        {
            return url;
        }

        context.AddChangeEvidence(
            this,
            manifestPath,
            fieldName,
            "normalized GitHub license/copyright link to the stable blob/HEAD form",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"{documentName}: normalized {fieldName} to the stable blob/HEAD form.");
        return normalized;
    }

    private static string? TryNormalize(string url)
    {
        Match shaBlob = ShaPinnedBlob().Match(url);
        if (shaBlob.Success)
        {
            return $"https://github.com/{shaBlob.Groups["owner"].Value}/{shaBlob.Groups["repo"].Value}/blob/HEAD/{shaBlob.Groups["path"].Value}";
        }

        Match raw = RawGitHubUserContent().Match(url);
        if (raw.Success)
        {
            return $"https://github.com/{raw.Groups["owner"].Value}/{raw.Groups["repo"].Value}/blob/HEAD/{raw.Groups["path"].Value}";
        }

        return null;
    }

    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/blob/(?<sha>[0-9a-fA-F]{40})/(?<path>.+)$")]
    private static partial Regex ShaPinnedBlob();

    [GeneratedRegex(@"^https?://raw\.githubusercontent\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?<ref>[^/]+)/(?<path>.+)$")]
    private static partial Regex RawGitHubUserContent();
}
