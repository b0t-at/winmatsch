using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BinMatch.Rules;
using BinMatch.Rules.OverridePacks;
using BinMatch.Rules.Policy;
using BinMatch.Validation;
using BinMatch.Workflows.Discovery;
using BinMatch.Workflows.Mapping;

namespace BinMatch.Workflows.Operations;

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
