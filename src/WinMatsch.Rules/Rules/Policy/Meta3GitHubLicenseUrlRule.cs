using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-3: normalizes GitHub license/copyright URLs. <c>raw.githubusercontent.com</c> links
/// (which render as plain text) are rewritten to the HTML <c>blob</c> form, preserving the
/// pinned ref so the link keeps resolving even when the file moved at HEAD. Rewrites to the
/// stable <c>blob/HEAD</c> form happen only when the resulting URL is confirmed reachable via
/// <see cref="PolicyEvidence.ConfirmedUrls"/> — policy rules have no live API access, and an
/// unverified HEAD rewrite has shipped hard 404s (a pinned file that was moved or deleted at
/// HEAD). Full-40-hex commit-pinned <c>blob</c> links are likewise moved to <c>blob/HEAD</c>
/// only with confirmation; branch-named blob links are left alone — renaming a default branch
/// is the publisher's decision, not this rule's.
/// </summary>
public sealed partial class Meta3GitHubLicenseUrlRule : IRule
{
    private readonly PolicyEvidence _evidence;

    public Meta3GitHubLicenseUrlRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Meta3;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Info;

    public string Description => "Normalizes GitHub license/copyright links to stable, reachable blob URLs.";

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
            "normalized GitHub license/copyright link to a stable blob form",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"{documentName}: normalized {fieldName} to a stable blob form.");
        return normalized;
    }

    private string? TryNormalize(string url)
    {
        Match shaBlob = ShaPinnedBlob().Match(url);
        if (shaBlob.Success)
        {
            // Only unpin to HEAD when the HEAD URL is confirmed reachable; a commit-pinned
            // blob link still works, while an unverified HEAD rewrite may 404.
            string head = $"https://github.com/{shaBlob.Groups["owner"].Value}/{shaBlob.Groups["repo"].Value}/blob/HEAD/{shaBlob.Groups["path"].Value}";
            return _evidence.IsUrlConfirmed(head) ? head : url;
        }

        Match raw = RawGitHubUserContent().Match(url);
        if (raw.Success)
        {
            string head = $"https://github.com/{raw.Groups["owner"].Value}/{raw.Groups["repo"].Value}/blob/HEAD/{raw.Groups["path"].Value}";
            if (_evidence.IsUrlConfirmed(head))
            {
                return head;
            }

            // Preserve the pinned ref: the blob form renders as HTML and keeps resolving
            // even when the file was moved or deleted at HEAD.
            return $"https://github.com/{raw.Groups["owner"].Value}/{raw.Groups["repo"].Value}/blob/{raw.Groups["ref"].Value}/{raw.Groups["path"].Value}";
        }

        return null;
    }

    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/blob/(?<sha>[0-9a-fA-F]{40})/(?<path>.+)$")]
    private static partial Regex ShaPinnedBlob();

    [GeneratedRegex(@"^https?://raw\.githubusercontent\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?<ref>[^/]+)/(?<path>.+)$")]
    private static partial Regex RawGitHubUserContent();
}
