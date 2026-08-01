using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMatsch.Cli.Hosting;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Mutations;

internal static partial class MutationOutput
{
    public static void Write(
        CommandContext context,
        WorkflowOperationResult local,
        GitHubLifecycleResult? remote)
    {
        context.Output.WriteFormatted(
            writer => WriteText(writer, local, remote),
            json => WriteJson(json, local, remote));
    }

    private static void WriteText(
        TextWriter writer,
        WorkflowOperationResult local,
        GitHubLifecycleResult? remote)
    {
        LocalOperationPlan plan = local.Plan;
        writer.WriteLine($"Operation: {plan.Operation}");
        writer.WriteLine($"Package: {plan.PackageIdentifier} {plan.PackageVersion}");
        writer.WriteLine($"Result: {ToKebab(local.Code)}");
        writer.WriteLine($"Applied: {local.Applied.ToString().ToLowerInvariant()}");
        writer.WriteLine($"Output: {plan.OutputDirectory}");
        writer.WriteLine("Changes:");
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static value => value.RepositoryPath,
                     StringComparer.Ordinal))
        {
            writer.WriteLine($"  {ToKebab(change.Kind)} {change.RepositoryPath}");
        }

        WriteQuestions(writer, plan);
        WriteRules(writer, plan);
        WriteValidation(writer, plan);
        WritePreview(writer, plan);
        if (remote is not null)
        {
            writer.WriteLine($"Remote result: {ToKebab(remote.Code)}");
            foreach (PlannedRemoteOperation operation in remote.Plan.Operations)
            {
                writer.WriteLine(
                    $"  remote {ToKebab(operation.Kind)} {operation.Target}: {Redact(operation.Description)}");
            }

            WriteRemoteState(writer, remote.RemoteState);
            foreach (GitHubLifecycleDiagnostic diagnostic in remote.Diagnostics)
            {
                writer.WriteLine(
                    $"  remote finding {diagnostic.Code}: {Redact(diagnostic.Message)}"
                    + (diagnostic.Path is null ? "" : $" [{Redact(diagnostic.Path)}]"));
            }
        }
    }

    private static void WriteQuestions(TextWriter writer, LocalOperationPlan plan)
    {
        foreach (WorkflowQuestion question in plan.Questions)
        {
            writer.WriteLine(
                $"Question {question.Code}: {Redact(question.Prompt)}"
                + (question.Path is null ? "" : $" [{Redact(question.Path)}]"));
        }
    }

    private static void WriteRules(TextWriter writer, LocalOperationPlan plan)
    {
        writer.WriteLine("Rules:");
        foreach (var execution in plan.Rules.Executions)
        {
            writer.WriteLine(
                $"  {execution.RuleId}: {ToKebab(execution.Mode)} ({ToKebab(execution.ModeSource)})");
        }

        foreach (var change in plan.Rules.Changes)
        {
            writer.WriteLine(
                $"  change {change.RuleId} {change.ManifestPath}:{change.FieldPath}"
                + $" {Redact(change.Before)} -> {Redact(change.After)}");
        }

        foreach (var review in plan.Rules.Reviews)
        {
            writer.WriteLine(
                $"  review {review.ManifestPath}:{review.FieldPath}"
                + $" human={Redact(review.HumanValue)} generated={Redact(review.GeneratedValue)}");
        }
    }

    private static void WriteValidation(TextWriter writer, LocalOperationPlan plan)
    {
        writer.WriteLine("Validation:");
        foreach (ValidationFinding finding in plan.Validation.Findings)
        {
            writer.WriteLine(
                $"  {ToKebab(finding.Severity)} {finding.Code}: {Redact(finding.Message)}"
                + (finding.Path is null ? "" : $" [{Redact(finding.Path)}]"));
        }
    }

    private static void WritePreview(TextWriter writer, LocalOperationPlan plan)
    {
        writer.WriteLine("Preview:");
        Dictionary<string, RawManifestDocument> before = plan.BeforeDocuments.ToDictionary(
            static document => document.RepositoryPath,
            StringComparer.Ordinal);
        Dictionary<string, RawManifestDocument> after = plan.AfterDocuments.ToDictionary(
            static document => document.RepositoryPath,
            StringComparer.Ordinal);
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static value => value.RepositoryPath,
                     StringComparer.Ordinal))
        {
            writer.WriteLine($"--- {change.RepositoryPath}");
            if (before.TryGetValue(change.RepositoryPath, out RawManifestDocument? old))
            {
                WriteDocumentLines(writer, "-", old);
            }

            writer.WriteLine($"+++ {change.RepositoryPath}");
            if (after.TryGetValue(change.RepositoryPath, out RawManifestDocument? updated))
            {
                WriteDocumentLines(writer, "+", updated);
            }
        }
    }

    private static void WriteDocumentLines(
        TextWriter writer,
        string prefix,
        RawManifestDocument document)
    {
        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(document.Content.AsSpan());
        }
        catch (DecoderFallbackException)
        {
            writer.WriteLine($"{prefix}<non-UTF8 content>");
            return;
        }

        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            writer.WriteLine($"{prefix}{Redact(line)}");
        }
    }

    private static void WriteRemoteState(TextWriter writer, RemoteMutationState state)
    {
        if (state.Fork is not null)
        {
            writer.WriteLine($"  fork: {state.Fork}");
        }

        if (state.BranchName is not null)
        {
            writer.WriteLine($"  branch: {Redact(state.BranchName)}");
        }

        if (state.CommitSha is not null)
        {
            writer.WriteLine($"  commit: {state.CommitSha}");
        }

        if (state.PullRequestNumber is not null)
        {
            writer.WriteLine($"  pull request: #{state.PullRequestNumber}");
        }

        if (state.PullRequestUri is not null)
        {
            writer.WriteLine($"  pull request URL: {state.PullRequestUri}");
        }

        writer.WriteLine(
            $"  remote outcome uncertain: {state.RemoteOutcomeUncertain.ToString().ToLowerInvariant()}");
    }

    private static void WriteJson(
        Utf8JsonWriter json,
        WorkflowOperationResult local,
        GitHubLifecycleResult? remote)
    {
        LocalOperationPlan plan = local.Plan;
        json.WriteStartObject();
        json.WriteString("operation", plan.Operation);
        json.WriteString("packageIdentifier", plan.PackageIdentifier.Value);
        json.WriteString("packageVersion", plan.PackageVersion.Value);
        json.WriteString("result", ToKebab(local.Code));
        json.WriteBoolean("applied", local.Applied);
        json.WriteString("outputDirectory", plan.OutputDirectory);
        json.WriteBoolean("requiresReview", plan.RequiresReview);
        json.WriteStartArray("changes");
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static value => value.RepositoryPath,
                     StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("kind", ToKebab(change.Kind));
            json.WriteString("path", change.RepositoryPath);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("questions");
        foreach (WorkflowQuestion question in plan.Questions)
        {
            json.WriteStartObject();
            json.WriteString("code", question.Code);
            json.WriteString("prompt", Redact(question.Prompt));
            WriteNullable(json, "path", question.Path);
            json.WriteStartArray("options");
            foreach (string option in question.Options)
            {
                json.WriteStringValue(Redact(option));
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteRules(json, plan);
        WriteValidation(json, plan);
        WritePreview(json, plan);
        json.WritePropertyName("remote");
        if (remote is null)
        {
            json.WriteNullValue();
        }
        else
        {
            WriteRemote(json, remote);
        }

        json.WriteEndObject();
    }

    private static void WriteRules(Utf8JsonWriter json, LocalOperationPlan plan)
    {
        json.WriteStartObject("rules");
        json.WriteStartArray("executions");
        foreach (var execution in plan.Rules.Executions)
        {
            json.WriteStartObject();
            json.WriteString("id", execution.RuleId);
            json.WriteString("mode", ToKebab(execution.Mode));
            json.WriteString("source", ToKebab(execution.ModeSource));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("changes");
        foreach (var change in plan.Rules.Changes)
        {
            json.WriteStartObject();
            json.WriteString("id", change.RuleId);
            json.WriteString("path", change.ManifestPath);
            json.WriteString("field", change.FieldPath);
            WriteNullable(json, "before", Redact(change.Before));
            WriteNullable(json, "after", Redact(change.After));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("reviews");
        foreach (var review in plan.Rules.Reviews)
        {
            json.WriteStartObject();
            json.WriteString("path", review.ManifestPath);
            json.WriteString("field", review.FieldPath);
            WriteNullable(json, "human", Redact(review.HumanValue));
            WriteNullable(json, "generated", Redact(review.GeneratedValue));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteValidation(Utf8JsonWriter json, LocalOperationPlan plan)
    {
        json.WriteStartArray("validation");
        foreach (ValidationFinding finding in plan.Validation.Findings)
        {
            json.WriteStartObject();
            json.WriteString("code", finding.Code);
            json.WriteString("severity", ToKebab(finding.Severity));
            json.WriteString("message", Redact(finding.Message));
            WriteNullable(json, "path", Redact(finding.Path));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WritePreview(Utf8JsonWriter json, LocalOperationPlan plan)
    {
        json.WriteStartArray("preview");
        foreach (RawManifestDocument document in plan.AfterDocuments.OrderBy(
                     static value => value.RepositoryPath,
                     StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("path", document.RepositoryPath);
            string content;
            try
            {
                content = new UTF8Encoding(false, true).GetString(document.Content.AsSpan());
            }
            catch (DecoderFallbackException)
            {
                content = "<non-UTF8 content>";
            }

            json.WriteString("content", Redact(content));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteRemote(Utf8JsonWriter json, GitHubLifecycleResult remote)
    {
        json.WriteStartObject();
        json.WriteString("result", ToKebab(remote.Code));
        json.WriteBoolean("applied", remote.Applied);
        json.WriteStartArray("operations");
        foreach (PlannedRemoteOperation operation in remote.Plan.Operations)
        {
            json.WriteStartObject();
            json.WriteString("kind", ToKebab(operation.Kind));
            json.WriteString("target", operation.Target);
            json.WriteString("description", Redact(operation.Description));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartObject("state");
        WriteNullable(json, "fork", remote.RemoteState.Fork?.ToString());
        WriteNullable(json, "branch", Redact(remote.RemoteState.BranchName));
        WriteNullable(json, "commitSha", remote.RemoteState.CommitSha);
        if (remote.RemoteState.PullRequestNumber is long number)
        {
            json.WriteNumber("pullRequestNumber", number);
        }
        else
        {
            json.WriteNull("pullRequestNumber");
        }

        WriteNullable(json, "pullRequestUrl", remote.RemoteState.PullRequestUri?.AbsoluteUri);
        json.WriteBoolean("forkCreated", remote.RemoteState.ForkCreated);
        json.WriteBoolean("branchCreated", remote.RemoteState.BranchCreated);
        json.WriteBoolean("commitCreated", remote.RemoteState.CommitCreated);
        json.WriteBoolean("pullRequestCreated", remote.RemoteState.PullRequestCreated);
        json.WriteBoolean("outcomeUncertain", remote.RemoteState.RemoteOutcomeUncertain);
        json.WriteEndObject();
        json.WriteStartArray("diagnostics");
        foreach (GitHubLifecycleDiagnostic diagnostic in remote.Diagnostics)
        {
            json.WriteStartObject();
            json.WriteString("code", diagnostic.Code);
            json.WriteString("message", Redact(diagnostic.Message));
            WriteNullable(json, "path", Redact(diagnostic.Path));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static string? Redact(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string redacted = GitHubTokenPattern().Replace(value, "[REDACTED]");
        redacted = SecretAssignmentPattern().Replace(redacted, "$1$2[REDACTED]");
        redacted = AuthorizationPattern().Replace(redacted, "$1[REDACTED]");
        redacted = UriUserInfoPattern().Replace(redacted, "$1[REDACTED]@");
        return QueryValuePattern().Replace(redacted, "$1=[REDACTED]");
    }

    private static string ToKebab<T>(T value)
        where T : struct, Enum
        => Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1-$2")
            .ToLowerInvariant();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b")]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(
        @"(?i)\b(password|client[-_]?secret|access[-_]?token|refresh[-_]?token|token|secret|api[-_]?key|signature|sig|credential)(\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(
        @"(?i)\b(authorization\s*[:=]\s*(?:(?:bearer|basic|token)\s+)?)(?:""[^""]*""|'[^']*'|[^\s,;]+)")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)(https?://)[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UriUserInfoPattern();

    [GeneratedRegex(@"([?&][^=\s&#]+)=[^&\s#]+")]
    private static partial Regex QueryValuePattern();
}
