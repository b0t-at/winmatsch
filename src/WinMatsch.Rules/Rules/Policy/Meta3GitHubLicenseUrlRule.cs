using System.Text.RegularExpressions;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-3: normalizes GitHub license/copyright URLs. <c>raw.githubusercontent.com</c> links and
/// the <c>github.com/{owner}/{repo}/raw/{ref}/{path}</c> shorthand (which both render as plain
/// text) are rewritten to the HTML <c>blob</c> form, preserving the pinned ref so the link keeps
/// resolving even when the file moved at HEAD. Rewrites to the
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

        // "github.com/{owner}/{repo}/raw/{ref}/{path}" is an undocumented GitHub shorthand that
        // redirects to the raw file content - it works, but it is not the canonical HTML
        // rendering form used for License/CopyrightUrl elsewhere, so normalize it the same way
        // as the raw.githubusercontent.com form above.
        Match rawShorthand = GitHubRawShorthand().Match(url);
        if (rawShorthand.Success)
        {
            string head = $"https://github.com/{rawShorthand.Groups["owner"].Value}/{rawShorthand.Groups["repo"].Value}/blob/HEAD/{rawShorthand.Groups["path"].Value}";
            if (_evidence.IsUrlConfirmed(head))
            {
                return head;
            }

            return $"https://github.com/{rawShorthand.Groups["owner"].Value}/{rawShorthand.Groups["repo"].Value}/blob/{rawShorthand.Groups["ref"].Value}/{rawShorthand.Groups["path"].Value}";
        }

        return null;
    }

    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/blob/(?<sha>[0-9a-fA-F]{40})/(?<path>.+)$")]
    private static partial Regex ShaPinnedBlob();

    // The ref segment can be a plain branch/tag/sha (one path segment) or the fully-qualified
    // "refs/heads/<name>"/"refs/tags/<name>" form, whose name itself may contain slashes (e.g.
    // "refs/heads/release/1.0"). Without the qualified alternatives first, the single-segment
    // fallback greedily claims only "refs" as the ref and leaves "heads/main" glued to the
    // start of <path>, which duplicates HEAD/heads/main when the blob/HEAD form is built.
    [GeneratedRegex(@"^https?://raw\.githubusercontent\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?:refs/(?:heads|tags)/(?<ref>.+?)|(?<ref>[^/]+))/(?<path>.+)$")]
    private static partial Regex RawGitHubUserContent();

    // Same ref-shape rules as RawGitHubUserContent (plain segment vs. fully-qualified
    // "refs/heads/<name>"/"refs/tags/<name>"), but for the "github.com/.../raw/..." shorthand
    // instead of the raw.githubusercontent.com host.
    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/raw/(?:refs/(?:heads|tags)/(?<ref>.+?)|(?<ref>[^/]+))/(?<path>.+)$")]
    private static partial Regex GitHubRawShorthand();
}
