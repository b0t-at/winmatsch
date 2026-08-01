using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace WinMatsch.Workflows.GitHub;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(GitHubLifecycleOutput))]
[JsonSerializable(typeof(GitHubMaintenanceResult))]
[JsonSerializable(typeof(GitHubCompleteResult))]
[JsonSerializable(typeof(FeedbackResult))]
[JsonSerializable(typeof(ImmutableArray<RemoveDeadVersionPlan>))]
public sealed partial class GitHubWorkflowJsonContext : JsonSerializerContext;
