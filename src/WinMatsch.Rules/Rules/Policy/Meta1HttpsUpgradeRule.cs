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
            string manifestPath = PolicyValues.GetLocaleManifestPath(context.Manifests, locale);
            locale.PublisherUrl = Process(context, locale.PublisherUrl, documentName, manifestPath, nameof(locale.PublisherUrl));
            locale.PublisherSupportUrl = Process(context, locale.PublisherSupportUrl, documentName, manifestPath, nameof(locale.PublisherSupportUrl));
            locale.PrivacyUrl = Process(context, locale.PrivacyUrl, documentName, manifestPath, nameof(locale.PrivacyUrl));
            locale.PackageUrl = Process(context, locale.PackageUrl, documentName, manifestPath, nameof(locale.PackageUrl));
            locale.LicenseUrl = Process(context, locale.LicenseUrl, documentName, manifestPath, nameof(locale.LicenseUrl));
            locale.CopyrightUrl = Process(context, locale.CopyrightUrl, documentName, manifestPath, nameof(locale.CopyrightUrl));
            locale.ReleaseNotesUrl = Process(context, locale.ReleaseNotesUrl, documentName, manifestPath, nameof(locale.ReleaseNotesUrl));
            locale.PurchaseUrl = Process(context, locale.PurchaseUrl, documentName, manifestPath, nameof(locale.PurchaseUrl));

            if (locale.Agreements is { } agreements)
            {
                for (int i = 0; i < agreements.Count; i++)
                {
                    agreements[i].AgreementUrl = Process(
                        context, agreements[i].AgreementUrl, documentName, manifestPath, $"Agreements[{i}].AgreementUrl");
                }
            }

            if (locale.Documentations is { } documentations)
            {
                for (int i = 0; i < documentations.Count; i++)
                {
                    documentations[i].DocumentUrl = Process(
                        context, documentations[i].DocumentUrl, documentName, manifestPath, $"Documentations[{i}].DocumentUrl");
                }
            }

            if (locale.Icons is { } icons)
            {
                for (int i = 0; i < icons.Count; i++)
                {
                    icons[i].IconUrl = Process(
                        context, icons[i].IconUrl, documentName, manifestPath, $"Icons[{i}].IconUrl");
                }
            }
        }
    }

    private string? Process(ManifestContext context, string? url, string documentName, string manifestPath, string fieldName)
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
        context.AddChangeEvidence(
            this,
            manifestPath,
            fieldName,
            "workflow HTTPS probe confirmed the https variant answers",
            RuleChangeConfidence.High);
        context.AddTrace(this, $"{documentName}: upgraded {fieldName} to https (probe evidence confirmed).");
        return upgraded;
    }
}
