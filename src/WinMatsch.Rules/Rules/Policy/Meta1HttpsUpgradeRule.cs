using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-1: upgrades <c>http://</c> metadata URLs in the locale manifests to <c>https://</c>,
/// but only when a workflow-side probe confirmed the HTTPS variant answers
/// (<see cref="PolicyEvidence.HttpsUpgradeConfirmations"/>). Without probe evidence the URL is
/// left untouched and a finding names the missing probe — the rule never mutates speculatively
/// and never performs network I/O itself.
/// </summary>
public sealed class Meta1HttpsUpgradeRule : IRule
{
    private const string HttpPrefix = "http://";

    private readonly PolicyEvidence _evidence;

    public Meta1HttpsUpgradeRule(PolicyEvidence? evidence = null)
    {
        _evidence = evidence ?? PolicyEvidence.Empty;
    }

    public string Id => RuleCatalogueIds.Meta1;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Upgrades http metadata URLs to https when probe evidence confirms the https variant.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach ((LocaleManifest locale, string documentName) in PolicyValues.EnumerateLocales(context.Manifests))
        {
            locale.PublisherUrl = Process(context, locale.PublisherUrl, documentName, nameof(locale.PublisherUrl));
            locale.PublisherSupportUrl = Process(context, locale.PublisherSupportUrl, documentName, nameof(locale.PublisherSupportUrl));
            locale.PrivacyUrl = Process(context, locale.PrivacyUrl, documentName, nameof(locale.PrivacyUrl));
            locale.PackageUrl = Process(context, locale.PackageUrl, documentName, nameof(locale.PackageUrl));
            locale.LicenseUrl = Process(context, locale.LicenseUrl, documentName, nameof(locale.LicenseUrl));
            locale.CopyrightUrl = Process(context, locale.CopyrightUrl, documentName, nameof(locale.CopyrightUrl));
            locale.ReleaseNotesUrl = Process(context, locale.ReleaseNotesUrl, documentName, nameof(locale.ReleaseNotesUrl));
            locale.PurchaseUrl = Process(context, locale.PurchaseUrl, documentName, nameof(locale.PurchaseUrl));
        }
    }

    private string? Process(ManifestContext context, string? url, string documentName, string fieldName)
    {
        if (url is null || !url.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!_evidence.IsHttpsUpgradeConfirmed(url))
        {
            context.AddFinding(this, RuleSeverity.Warning,
                $"{fieldName} uses http:// but no HTTPS probe evidence was supplied; the URL was not changed. Supply probe evidence or fix the URL upstream.",
                documentName);
            return url;
        }

        string upgraded = "https://" + url[HttpPrefix.Length..];
        context.AddTrace(this, $"{documentName}: upgraded {fieldName} to https (probe evidence confirmed).");
        return upgraded;
    }
}
