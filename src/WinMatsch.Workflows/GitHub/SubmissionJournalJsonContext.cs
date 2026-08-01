using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace WinMatsch.Workflows.GitHub;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SubmissionJournalEntry))]
[JsonSerializable(typeof(SubmissionJournalEnvelope))]
[JsonSerializable(typeof(SubmissionPreparedIntent))]
[JsonSerializable(typeof(ImmutableArray<SubmissionJournalEntry>))]
internal sealed partial class SubmissionJournalJsonContext : JsonSerializerContext;

internal sealed record SubmissionJournalEnvelope(
    string Payload,
    string Sha256);

internal sealed record SubmissionPreparedIntent(
    SubmissionJournalEntry Entry);
