using System.Collections.Immutable;
using System.Text.Json.Serialization;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Operations;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(RuleRuntimeConfiguration))]
[JsonSerializable(typeof(OverridePackSet))]
[JsonSerializable(typeof(PolicyEvidence))]
[JsonSerializable(typeof(PackageLocaleMetadata))]
[JsonSerializable(typeof(ReleaseRequest))]
[JsonSerializable(typeof(ImmutableArray<DiscoveredAsset>))]
[JsonSerializable(typeof(ImmutableArray<UrlOverride>))]
[JsonSerializable(typeof(RuleRunSummary))]
[JsonSerializable(typeof(ImmutableArray<ValidationFinding>))]
[JsonSerializable(typeof(ImmutableArray<WorkflowAuditEntry>))]
[JsonSerializable(typeof(LearnedOverridePlan))]
[JsonSerializable(typeof(WorkflowQuestion))]
internal sealed partial class WorkflowFingerprintJsonContext : JsonSerializerContext;
