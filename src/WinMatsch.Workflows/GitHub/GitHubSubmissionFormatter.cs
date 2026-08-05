using System.Text;
using System.Text.RegularExpressions;
using WinMatsch.Core;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Workflows.GitHub;

public static partial class GitHubSubmissionFormatter
{
    public static string CreateTitle(
        GitHubManifestOperation operation,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion,
        string? customTitle = null)
    {
        string canonical =
            $"{GetCanonicalPrefix(operation)} {packageIdentifier.Value} version {packageVersion.Value}";
        return string.IsNullOrWhiteSpace(customTitle)
            ? canonical
            : $"{canonical} - {Redact(customTitle.Trim())}";
    }

    public static bool IsCanonicalTitleFor(
        string title,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        foreach (GitHubManifestOperation operation in Enum.GetValues<GitHubManifestOperation>())
        {
            string canonical = CreateTitle(operation, packageIdentifier, packageVersion);
            if (string.Equals(title, canonical, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith(canonical + " - ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCanonicalTitleFor(
        GitHubManifestOperation operation,
        string title,
        PackageIdentifier packageIdentifier,
        PackageVersion packageVersion)
    {
        ArgumentNullException.ThrowIfNull(title);
        string canonical = CreateTitle(operation, packageIdentifier, packageVersion);
        return string.Equals(title, canonical, StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(canonical + " - ", StringComparison.OrdinalIgnoreCase);
    }

    public static string CreateBody(GitHubSubmissionRequest request, string versionDirectory)
    {
        LocalOperationPlan plan = request.LocalPlan;
        var builder = new StringBuilder();
        builder.AppendLine($"<!-- winmatsch:package={plan.PackageIdentifier.Value};version={plan.PackageVersion.Value} -->");
        builder.AppendLine($"<!-- winmatsch:operation={request.Operation} -->");
        builder.AppendLine(
            $"{GetHumanAction(request.Operation)} {plan.PackageIdentifier.Value} version {plan.PackageVersion.Value}.");
        builder.AppendLine($"Created with {GetCreatedWith(request.CreatedWith)}");
        if (!string.IsNullOrWhiteSpace(request.Resolves))
        {
            builder.AppendLine($"Resolves {NormalizeIssueReference(request.Resolves)}");
        }

        if (request.SupersedesPullRequestNumber is { } superseded)
        {
            builder.AppendLine($"Supersedes: #{superseded}");
        }

        return Redact(builder.ToString().TrimEnd());
    }

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = SecretAssignmentRegex().Replace(value, "$1=[REDACTED]");
        redacted = AuthorizationRegex().Replace(redacted, "$1 [REDACTED]");
        redacted = UriUserInfoRegex().Replace(redacted, "$1[REDACTED]@");
        return QueryValueRegex().Replace(redacted, "$1=[REDACTED]");
    }

    private static string GetCanonicalPrefix(GitHubManifestOperation operation)
        => operation switch
        {
            GitHubManifestOperation.New => "New version:",
            GitHubManifestOperation.Update or GitHubManifestOperation.Replace => "Update version:",
            GitHubManifestOperation.Add => "Add version:",
            GitHubManifestOperation.Remove => "Remove version:",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    public static bool TryGetOperation(string? body, out GitHubManifestOperation operation)
    {
        operation = default;
        const string markerPrefix = "<!-- winmatsch:operation=";
        string? marker = body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(markerPrefix, StringComparison.Ordinal));
        if (marker is not null)
        {
            return marker.EndsWith("-->", StringComparison.Ordinal)
                && TryParseNamedOperation(marker[markerPrefix.Length..^3].Trim(), out operation);
        }

        const string legacyPrefix = "Operation:";
        string? legacyLine = body?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(legacyPrefix, StringComparison.Ordinal));
        return legacyLine is not null
            && TryParseNamedOperation(legacyLine[legacyPrefix.Length..].Trim(), out operation);
    }

    public static bool HasOperationMetadata(string? body)
        => body?.Contains("<!-- winmatsch:operation=", StringComparison.Ordinal) == true
            || body?.Split('\n', StringSplitOptions.TrimEntries)
                .Any(static line => line.StartsWith(
                    "Operation:",
                    StringComparison.OrdinalIgnoreCase)) == true;

    private static bool TryParseNamedOperation(
        string value,
        out GitHubManifestOperation operation)
    {
        foreach (GitHubManifestOperation candidate in Enum.GetValues<GitHubManifestOperation>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                operation = candidate;
                return true;
            }
        }

        operation = default;
        return false;
    }

    private static string GetHumanAction(GitHubManifestOperation operation)
        => operation switch
        {
            GitHubManifestOperation.New or GitHubManifestOperation.Add => "Add",
            GitHubManifestOperation.Update or GitHubManifestOperation.Replace => "Update",
            GitHubManifestOperation.Remove => "Remove",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static string NormalizeIssueReference(string resolves)
    {
        string reference = resolves.Trim();
        return long.TryParse(reference, out long number) && number > 0
            ? $"#{number}"
            : reference;
    }

    private static string GetCreatedWith(string createdWith)
    {
        if (!string.Equals(createdWith, "winmatsch", StringComparison.OrdinalIgnoreCase))
        {
            return createdWith;
        }

        Version? version = typeof(GitHubSubmissionFormatter).Assembly.GetName().Version;
        return version is null
            ? createdWith
            : $"{createdWith} v{version.Major}.{version.Minor}.{version.Build}";
    }

    [GeneratedRegex(@"(?i)\b(token|password|secret|client_secret|access_token)\s*=\s*[^\s&]+")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(authorization\s*:?)\s+\S+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(https?://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(@"([?&][^=\s&#]+)=[^&\s#]+")]
    private static partial Regex QueryValueRegex();
}
