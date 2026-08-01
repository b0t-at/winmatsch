using System.Text;
using System.Text.RegularExpressions;
using WinMatsch.Core;
using WinMatsch.Rules;
using WinMatsch.Validation;
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
        string canonical = $"{operation}: {packageIdentifier.Value} version {packageVersion.Value}";
        return string.IsNullOrWhiteSpace(customTitle)
            ? canonical
            : $"{canonical} - {Redact(customTitle.Trim())}";
    }

    public static string CreateBody(GitHubSubmissionRequest request, string versionDirectory)
    {
        LocalOperationPlan plan = request.LocalPlan;
        var builder = new StringBuilder();
        builder.AppendLine($"<!-- winmatsch:package={plan.PackageIdentifier.Value};version={plan.PackageVersion.Value} -->");
        builder.AppendLine($"Created with: {request.CreatedWith}");
        builder.AppendLine($"Operation: {request.Operation}");
        builder.AppendLine(
            $"Target repository: {request.TargetRepository?.ToString() ?? "authenticated-user fork"}");
        builder.AppendLine($"Manifest path: `{versionDirectory}`");
        builder.AppendLine($"Dry run: {request.ExecutionMode == WorkflowExecutionMode.Plan}");
        builder.AppendLine($"Skip PR check: {request.Policy.SkipPullRequestCheck}");
        if (!string.IsNullOrWhiteSpace(request.Resolves))
        {
            builder.AppendLine($"Resolves: {request.Resolves.Trim()}");
        }

        if (request.SupersedesPullRequestNumber is { } superseded)
        {
            builder.AppendLine($"Supersedes: #{superseded}");
        }

        if (!request.VanityUrlAnnotations.IsEmpty)
        {
            builder.AppendLine("Vanity URL annotations:");
            foreach (string annotation in request.VanityUrlAnnotations)
            {
                builder.AppendLine($"- {annotation}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Validation");
        AppendValidation(builder, plan.Validation);
        builder.AppendLine();
        builder.AppendLine("## Rules");
        AppendRules(builder, plan.Rules);
        builder.AppendLine();
        builder.AppendLine("## Changes");
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(static item => item.RepositoryPath, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {change.Kind}: `{change.RepositoryPath}`");
        }

        if (!string.IsNullOrWhiteSpace(request.Policy.DuplicateHashes.OverrideAnnotation))
        {
            builder.AppendLine();
            builder.AppendLine($"Duplicate-hash override: {request.Policy.DuplicateHashes.OverrideAnnotation}");
        }

        return Redact(builder.ToString().TrimEnd());
    }

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string redacted = SecretAssignmentRegex().Replace(value, "$1=[REDACTED]");
        redacted = AuthorizationRegex().Replace(redacted, "$1 [REDACTED]");
        redacted = UriUserInfoRegex().Replace(redacted, "$1[REDACTED]@");
        return SensitiveQueryRegex().Replace(redacted, "$1=[REDACTED]");
    }

    private static void AppendValidation(StringBuilder builder, ValidationReport validation)
    {
        builder.AppendLine($"Status: {(validation.IsValid ? "passed" : "failed")}");
        if (validation.Findings.Count == 0)
        {
            builder.AppendLine("- No findings.");
            return;
        }

        foreach (ValidationFinding finding in validation.Findings)
        {
            builder.Append("- ")
                .Append(finding.Code)
                .Append(" [")
                .Append(finding.Severity)
                .Append("]: ")
                .AppendLine(finding.Message);
        }
    }

    private static void AppendRules(StringBuilder builder, RuleRunSummary rules)
    {
        if (rules.Executions.IsEmpty)
        {
            builder.AppendLine("- No rule executions.");
        }
        else
        {
            foreach (RuleExecution execution in rules.Executions)
            {
                builder.AppendLine($"- {execution.RuleId}: {execution.Mode} ({execution.ModeSource})");
            }
        }

        builder.AppendLine($"Changes: {rules.Changes.Length}");
        builder.AppendLine($"Reviews: {rules.Reviews.Length}");
    }

    [GeneratedRegex(@"(?i)\b(token|password|secret|client_secret|access_token)\s*=\s*[^\s&]+")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(authorization\s*:?)\s+\S+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(https?://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|access_token|key|secret|signature))=[^&\s]+")]
    private static partial Regex SensitiveQueryRegex();
}
