using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMatsch.Cli.Hosting;
using WinMatsch.Cli.Output;
using WinMatsch.Validation;
using WinMatsch.Workflows.GitHub;
using WinMatsch.Workflows.Operations;

namespace WinMatsch.Cli.Commands.Mutations;

internal static class MutationOutput
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
        if (!string.IsNullOrWhiteSpace(local.ErrorMessage))
        {
            writer.WriteLine($"Warning: {Redact(local.ErrorMessage)}");
        }

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
        WriteAudit(writer, plan);
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

    private static void WriteAudit(TextWriter writer, LocalOperationPlan plan)
    {
        writer.WriteLine("Audit:");
        foreach (WorkflowAuditEntry entry in plan.Audit)
        {
            writer.WriteLine(
                $"  {entry.Code}: {Redact(entry.Message)}"
                + (entry.Provenance is null ? "" : $" [{Redact(entry.Provenance)}]"));
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
        CliJson.WriteEnum(json, "result", local.Code, ToKebab(local.Code));
        json.WriteBoolean("applied", local.Applied);
        if (!string.IsNullOrWhiteSpace(local.ErrorMessage))
        {
            json.WriteString("warning", Redact(local.ErrorMessage));
        }

        json.WriteString("outputDirectory", plan.OutputDirectory);
        json.WriteBoolean("reviewApproved", plan.ReviewApproved);
        json.WriteBoolean("requiresReview", plan.RequiresReview);
        json.WriteStartArray("changes");
        foreach (WorkflowFileChange change in plan.FileChanges.OrderBy(
                     static value => value.RepositoryPath,
                     StringComparer.Ordinal))
        {
            json.WriteStartObject();
            CliJson.WriteEnum(json, "kind", change.Kind, ToKebab(change.Kind));
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
            WriteNullable(json, "path", Redact(question.Path));
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
        WriteAudit(json, plan);
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
            CliJson.WriteEnum(json, "mode", execution.Mode, ToKebab(execution.Mode));
            CliJson.WriteEnum(
                json,
                "source",
                execution.ModeSource,
                ToKebab(execution.ModeSource));
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
            CliJson.WriteEnum(
                json,
                "severity",
                finding.Severity,
                ToKebab(finding.Severity));
            json.WriteString("message", Redact(finding.Message));
            WriteNullable(json, "path", Redact(finding.Path));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteAudit(Utf8JsonWriter json, LocalOperationPlan plan)
    {
        json.WriteStartArray("audit");
        foreach (WorkflowAuditEntry entry in plan.Audit)
        {
            json.WriteStartObject();
            json.WriteString("code", entry.Code);
            json.WriteString("message", Redact(entry.Message));
            WriteNullable(json, "provenance", Redact(entry.Provenance));
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
        CliJson.WriteEnum(json, "result", remote.Code, ToKebab(remote.Code));
        json.WriteBoolean("applied", remote.Applied);
        json.WriteStartArray("operations");
        foreach (PlannedRemoteOperation operation in remote.Plan.Operations)
        {
            json.WriteStartObject();
            CliJson.WriteEnum(
                json,
                "kind",
                operation.Kind,
                ToKebab(operation.Kind));
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
        => value is null
            ? null
            : CliRedactor.RedactUrl(value, redactAllQueryValues: true);

    private static string ToKebab<T>(T value)
        where T : struct, Enum
        => Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1-$2")
            .ToLowerInvariant();

}
