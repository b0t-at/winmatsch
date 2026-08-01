using System.Text;
using WinMatsch.Core;

namespace WinMatsch.Rules.Policy;

/// <summary>
/// META-4: bounded release-notes sanitization. Leading <c>- </c> / <c>* </c> bullets become
/// <c>• </c> (the transform that repeatedly unblocked winget-pkgs validation; controllable via
/// the <c>sanitizeBullets</c> flag — register a second instance under a sub-id if the runtime
/// should flag it separately, since <see cref="RuleRuntimeConfiguration"/> resolves modes per
/// rule id only). Lines that look like <c>key: value</c> inside the block scalar get their
/// first <c>": "</c> replaced with a fullwidth colon. Notes longer than the configured maximum
/// are truncated at the last paragraph boundary, or omitted entirely when no boundary exists.
/// A ReleaseNotesUrl still embedding the previous version is swapped to the new version only
/// when the swapped URL appears in <see cref="PolicyEvidence.ConfirmedUrls"/>; otherwise a
/// finding is emitted and nothing is mutated.
/// </summary>
public sealed class Meta4ReleaseNotesSanitizeRule : IRule
{
    public const int DefaultMaximumLength = 10_000;

    private readonly PolicyEvidence _evidence;
    private readonly bool _sanitizeBullets;
    private readonly int _maximumLength;

    public Meta4ReleaseNotesSanitizeRule(
        PolicyEvidence? evidence = null,
        bool sanitizeBullets = true,
        int maximumLength = DefaultMaximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        _evidence = evidence ?? PolicyEvidence.Empty;
        _sanitizeBullets = sanitizeBullets;
        _maximumLength = maximumLength;
    }

    public string Id => RuleCatalogueIds.Meta4;

    public RuleCategory Category => RuleCategory.Policy;

    public RuleSeverity Severity => RuleSeverity.Warning;

    public string Description => "Sanitizes release-notes bullets/colons, bounds their length, and verifies the ReleaseNotesUrl version.";

    public void Apply(ManifestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? oldVersion = context.Previous?.Installer.PackageVersion?.Value;
        string? newVersion = context.Manifests.Installer.PackageVersion?.Value;

        foreach ((LocaleManifest locale, string documentName) in PolicyValues.EnumerateLocales(context.Manifests))
        {
            string manifestPath = PolicyValues.GetLocaleManifestPath(context.Manifests, locale);
            locale.ReleaseNotes = SanitizeNotes(context, locale.ReleaseNotes, documentName, manifestPath);
            locale.ReleaseNotesUrl = CheckReleaseNotesUrl(context, locale.ReleaseNotesUrl, oldVersion, newVersion, documentName, manifestPath);
        }
    }

    private string? SanitizeNotes(ManifestContext context, string? notes, string documentName, string manifestPath)
    {
        if (notes is null)
        {
            return null;
        }

        string sanitized = SanitizeLines(context, notes, documentName);
        if (sanitized.Length > _maximumLength)
        {
            int boundary = sanitized.LastIndexOf("\n\n", _maximumLength, StringComparison.Ordinal);
            if (boundary <= 0)
            {
                context.AddChangeEvidence(
                    this,
                    manifestPath,
                    "ReleaseNotes",
                    $"release notes exceed {_maximumLength} characters with no paragraph boundary; field omitted",
                    RuleChangeConfidence.High);
                context.AddFinding(this, RuleSeverity.Warning,
                    $"ReleaseNotes exceed {_maximumLength} characters and contain no paragraph boundary to truncate at; the field was omitted.",
                    documentName);
                return null;
            }

            sanitized = sanitized[..boundary];
            context.AddTrace(this, $"{documentName}: truncated ReleaseNotes at the last paragraph boundary before {_maximumLength} characters.");
        }

        if (!string.Equals(sanitized, notes, StringComparison.Ordinal))
        {
            context.AddChangeEvidence(
                this,
                manifestPath,
                "ReleaseNotes",
                "bounded release-notes sanitization (bullets, key-value colons, maximum length)",
                RuleChangeConfidence.High);
        }

        return sanitized;
    }

    private string SanitizeLines(ManifestContext context, string notes, string documentName)
    {
        string[] lines = notes.Split('\n');
        bool bullets = false;
        bool colons = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (_sanitizeBullets && TrySanitizeBullet(line, out string bulleted))
            {
                lines[i] = line = bulleted;
                bullets = true;
            }

            if (TryGuardColon(line, out string guarded))
            {
                lines[i] = guarded;
                colons = true;
            }
        }

        if (bullets)
        {
            context.AddTrace(this, $"{documentName}: replaced leading '-'/'*' release-note bullets with '•'.");
        }

        if (colons)
        {
            context.AddTrace(this, $"{documentName}: replaced 'key: value'-looking colons in release notes with a fullwidth colon.");
        }

        return string.Join('\n', lines);
    }

    private static bool TrySanitizeBullet(string line, out string result)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent + 1 < line.Length
            && line[indent] is '-' or '*'
            && line[indent + 1] == ' ')
        {
            result = string.Concat(line.AsSpan(0, indent), "\u2022", line.AsSpan(indent + 1));
            return true;
        }

        result = line;
        return false;
    }

    private static bool TryGuardColon(string line, out string result)
    {
        int index = line.IndexOf(": ", StringComparison.Ordinal);
        // Require visible text before and after so plain prose sentences are untouched
        // only when the colon cannot be read as a YAML mapping key.
        if (index > 0 && index + 2 < line.Length && !char.IsWhiteSpace(line[index - 1]))
        {
            var builder = new StringBuilder(line.Length);
            builder.Append(line, 0, index).Append('\uFF1A').Append(line, index + 2, line.Length - index - 2);
            result = builder.ToString();
            return true;
        }

        result = line;
        return false;
    }

    private string? CheckReleaseNotesUrl(
        ManifestContext context,
        string? url,
        string? oldVersion,
        string? newVersion,
        string documentName,
        string manifestPath)
    {
        if (url is null || oldVersion is null || newVersion is null
            || string.Equals(oldVersion, newVersion, StringComparison.Ordinal)
            || !url.Contains(oldVersion, StringComparison.Ordinal)
            || url.Contains(newVersion, StringComparison.Ordinal))
        {
            return url;
        }

        string candidate = url.Replace(oldVersion, newVersion, StringComparison.Ordinal);
        if (_evidence.IsUrlConfirmed(candidate))
        {
            context.AddChangeEvidence(
                this,
                manifestPath,
                "ReleaseNotesUrl",
                $"confirmed-URL evidence for the {newVersion} release-notes page",
                RuleChangeConfidence.High);
            context.AddTrace(this, $"{documentName}: retargeted ReleaseNotesUrl from version {oldVersion} to {newVersion} (confirmed URL evidence).");
            return candidate;
        }

        context.AddFinding(this, RuleSeverity.Warning,
            $"ReleaseNotesUrl still references the previous version '{oldVersion}'; no confirmed-URL evidence for the '{newVersion}' variant was supplied, so it was not changed.",
            documentName);
        return url;
    }
}
