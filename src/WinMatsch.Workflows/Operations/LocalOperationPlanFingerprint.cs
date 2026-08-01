using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinMatsch.Rules;
using WinMatsch.Rules.OverridePacks;
using WinMatsch.Rules.Policy;
using WinMatsch.Validation;
using WinMatsch.Workflows.Discovery;
using WinMatsch.Workflows.Mapping;

namespace WinMatsch.Workflows.Operations;

public static class LocalOperationPlanFingerprint
{
    public static string Create(LocalOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var writer = new CanonicalHashWriter();
        writer.Add("format", 1);
        writer.Add("operation", plan.Operation);
        writer.Add("package", plan.PackageIdentifier.Value);
        writer.Add("version", plan.PackageVersion.Value);
        writer.Add("output", CanonicalPath(plan.OutputDirectory));
        writer.Add("warning-policy", plan.WarningPolicy.ToString());
        writer.Add("review-approved", plan.ReviewApproved ? "true" : "false");
        writer.Add("planning-inputs", plan.PlanningInputsFingerprint);
        writer.Add(
            "rules",
            Component(plan.RuleEvaluationFingerprint, plan.Rules));
        writer.Add(
            "validation",
            Component(plan.ValidationFingerprint, plan.Validation.Findings));
        writer.Add(
            "audit",
            Component(
                plan.AuditFingerprint,
                plan.Audit.Where(static entry =>
                    !string.Equals(entry.Code, "CREATED_AT", StringComparison.Ordinal))));
        writer.Add(
            "preflight",
            string.IsNullOrWhiteSpace(plan.PreflightEvidenceFingerprint)
                ? CreatePreflightFingerprint(plan.Preflight)
                : plan.PreflightEvidenceFingerprint);
        writer.Add(
            "learned-override",
            plan.LearnedOverrideFingerprint
                ?? (plan.LearnedOverride is null ? null : CreateComponent(plan.LearnedOverride)));

        writer.Add("change-count", plan.FileChanges.Length);
        foreach (WorkflowFileChange change in plan.FileChanges
                     .OrderBy(static change => change.RepositoryPath, StringComparer.Ordinal)
                     .ThenBy(static change => change.Kind))
        {
            writer.Add("change-kind", change.Kind.ToString());
            writer.Add("change-path", change.RepositoryPath);
            writer.Add("change-expected-state", change.ExpectedState.ToString());
            writer.Add("change-expected-sha256", change.ExpectedSha256);
            writer.Add("change-provenance", change.Provenance.ToString());
            writer.Add("change-content", change.Content.AsSpan());
        }

        AddDocuments(writer, "before", plan.BeforeDocuments);
        AddDocuments(writer, "after", plan.AfterDocuments);
        writer.Add("question-count", plan.Questions.Length);
        foreach (WorkflowQuestion question in plan.Questions)
        {
            writer.Add("question", CreateComponent(question));
        }

        if (plan.Release is null)
        {
            writer.Add("release", (string?)null);
        }
        else
        {
            writer.Add("release-repository", plan.Release.Repository.ToString());
            writer.Add("release-id", plan.Release.ReleaseId);
            writer.Add("release-updated", plan.Release.UpdatedAt.ToUniversalTime().ToString("O"));
        }

        return writer.Finish();
    }

    public static string CreateApprovalFingerprint(LocalOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var writer = new CanonicalHashWriter();
        writer.Add("approval-format", 1);
        writer.Add("operation", plan.Operation);
        writer.Add("package", plan.PackageIdentifier.Value);
        writer.Add("version", plan.PackageVersion.Value);
        writer.Add("output", CanonicalPath(plan.OutputDirectory));
        writer.Add("warning-policy", plan.WarningPolicy.ToString());
        writer.Add("planning-inputs", plan.PlanningInputsFingerprint);
        writer.Add(
            "rules",
            Component(plan.RuleEvaluationFingerprint, plan.Rules));
        writer.Add(
            "validation",
            Component(plan.ValidationFingerprint, plan.Validation.Findings));
        writer.Add(
            "preflight",
            string.IsNullOrWhiteSpace(plan.PreflightEvidenceFingerprint)
                ? CreatePreflightFingerprint(plan.Preflight)
                : plan.PreflightEvidenceFingerprint);
        foreach (WorkflowFileChange change in plan.FileChanges
                     .OrderBy(static change => change.RepositoryPath, StringComparer.Ordinal)
                     .ThenBy(static change => change.Kind))
        {
            writer.Add("change-kind", change.Kind.ToString());
            writer.Add("change-path", change.RepositoryPath);
            writer.Add("change-expected-state", change.ExpectedState.ToString());
            writer.Add("change-expected-sha256", change.ExpectedSha256);
            writer.Add("change-provenance", change.Provenance.ToString());
            writer.Add("change-content", change.Content.AsSpan());
        }

        AddDocuments(writer, "before", plan.BeforeDocuments);
        AddDocuments(writer, "after", plan.AfterDocuments);
        if (plan.Release is not null)
        {
            writer.Add("release-repository", plan.Release.Repository.ToString());
            writer.Add("release-id", plan.Release.ReleaseId);
            writer.Add("release-updated", plan.Release.UpdatedAt.ToUniversalTime().ToString("O"));
        }

        return writer.Finish();
    }

    public static string CreateRequestFingerprint(WorkflowOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var writer = new CanonicalHashWriter();
        writer.Add("request-type", request.GetType().Name);
        writer.Add("output", CanonicalPath(request.OutputDirectory));
        writer.Add("created-with", request.CreatedWith);
        writer.Add("warning-policy", request.WarningPolicy.ToString());
        writer.Add("network-mode", request.NetworkValidationMode.ToString());
        writer.Add("explain-rules", request.ExplainRules ? "true" : "false");
        writer.Add("rule-runtime", CreateComponent(request.RuleRuntime));
        writer.Add("override-packs", CreateComponent(request.OverridePacks));
        writer.Add("policy-evidence", CreateComponent(request.PolicyEvidence));
        switch (request)
        {
            case NewOperationRequest value:
                writer.Add("package", value.PackageIdentifier.Value);
                writer.Add("version", value.PackageVersion);
                writer.Add("release", CreateComponent(value.Release));
                writer.Add("assets", CreateComponent(value.Assets));
                writer.Add("locale", CreateComponent(value.Locale));
                writer.Add("url-overrides", CreateComponent(value.UrlOverrides));
                writer.Add("allow-shared-content", value.AllowSharedContentAcrossUrls ? "true" : "false");
                writer.Add("artifact-directory", value.ArtifactDirectory);
                AddInstallerArtifacts(writer, value.InstallerArtifacts);
                break;
            case UpdateOperationRequest value:
                writer.Add("package", value.PackageIdentifier.Value);
                writer.Add("previous-version", value.PreviousVersion.Value);
                writer.Add("version", value.PackageVersion);
                writer.Add("release", CreateComponent(value.Release));
                writer.Add("assets", CreateComponent(value.Assets));
                writer.Add("url-overrides", CreateComponent(value.UrlOverrides));
                writer.Add("replace", value.ReplacePreviousVersion ? "true" : "false");
                writer.Add("structural-rewrite", value.AllowStructuralRewrite ? "true" : "false");
                writer.Add("stable-url-change", value.AllowStableUrlContentChange ? "true" : "false");
                writer.Add("allow-shared-content", value.AllowSharedContentAcrossUrls ? "true" : "false");
                writer.Add("artifact-directory", value.ArtifactDirectory);
                AddInstallerArtifacts(writer, value.InstallerArtifacts);
                break;
            case RemoveOperationRequest value:
                writer.Add("package", value.PackageIdentifier.Value);
                writer.Add("version", value.PackageVersion.Value);
                break;
            case SubmitOperationRequest value:
                writer.Add("normalize", value.Normalize ? "true" : "false");
                writer.Add("artifact-directory", value.ArtifactDirectory);
                writer.Add(
                    "release-provenance-repository",
                    value.ReleaseProvenance?.Repository.ToString());
                writer.Add(
                    "release-provenance-id",
                    value.ReleaseProvenance?.ReleaseId.ToString(CultureInfo.InvariantCulture));
                writer.Add(
                    "release-provenance-updated",
                    value.ReleaseProvenance?.UpdatedAt.ToUniversalTime().ToString("O"));
                break;
            case NewLocaleOperationRequest value:
                writer.Add("package", value.PackageIdentifier.Value);
                writer.Add("version", value.PackageVersion.Value);
                writer.Add("locale", CreateComponent(value.Locale));
                break;
            case UpdateLocaleOperationRequest value:
                writer.Add("package", value.PackageIdentifier.Value);
                writer.Add("version", value.PackageVersion.Value);
                writer.Add("locale", CreateComponent(value.Locale));
                break;
            default:
                throw new ArgumentException("Unsupported workflow request.", nameof(request));
        }

        return writer.Finish();
    }

    public static string CreatePreflightFingerprint(WorkflowPreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var writer = new CanonicalHashWriter();
        writer.Add("warning-policy", request.Options.WarningPolicy.ToString());
        writer.Add("network-mode", request.Options.NetworkMode.ToString());
        AddInstallerArtifacts(writer, request.InstallerArtifacts);
        writer.Add("existing-count", request.ExistingVersions.Count);
        foreach (ExistingVersionSnapshot existing in request.ExistingVersions
                     .OrderBy(static item => item.PackageVersion, StringComparer.Ordinal))
        {
            writer.Add("existing-version", existing.PackageVersion);
            foreach (string displayVersion in existing.DisplayVersions.Order(StringComparer.Ordinal))
            {
                writer.Add("display-version", displayVersion);
            }
        }

        return writer.Finish();
    }

    private static void AddInstallerArtifacts(
        CanonicalHashWriter writer,
        ImmutableArray<InstallerArtifact> artifacts)
    {
        writer.Add("artifact-count", artifacts.Length);
        foreach (InstallerArtifact artifact in artifacts
                     .OrderBy(static item => item.InstallerUrl, StringComparer.Ordinal))
        {
            writer.Add("artifact-url", artifact.InstallerUrl);
            writer.Add("artifact-sha256", artifact.Download.Sha256.Value);
            writer.Add("artifact-size", artifact.Download.SizeInBytes);
            writer.Add("artifact-final-url", artifact.Download.FinalUrl);
            writer.Add("artifact-etag", artifact.Download.ETag);
            writer.Add(
                "artifact-last-modified",
                artifact.Download.LastModified?.ToUniversalTime().ToString("O"));
        }
    }

    public static string CreateComponent(object? value)
    {
        using var writer = new CanonicalHashWriter();
        JsonElement element = value switch
        {
            null => JsonSerializer.SerializeToElement<object?>(
                null,
                WorkflowFingerprintJsonContext.Default.Object),
            RuleRuntimeConfiguration item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.RuleRuntimeConfiguration),
            OverridePackSet item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.OverridePackSet),
            PolicyEvidence item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.PolicyEvidence),
            PackageLocaleMetadata item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.PackageLocaleMetadata),
            ReleaseRequest item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.ReleaseRequest),
            ImmutableArray<DiscoveredAsset> item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.ImmutableArrayDiscoveredAsset),
            ImmutableArray<UrlOverride> item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.ImmutableArrayUrlOverride),
            RuleRunSummary item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.RuleRunSummary),
            IEnumerable<ValidationFinding> item => Element(
                item.ToImmutableArray(),
                WorkflowFingerprintJsonContext.Default.ImmutableArrayValidationFinding),
            IEnumerable<WorkflowAuditEntry> item => Element(
                item.ToImmutableArray(),
                WorkflowFingerprintJsonContext.Default.ImmutableArrayWorkflowAuditEntry),
            LearnedOverridePlan item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.LearnedOverridePlan),
            WorkflowQuestion item => Element(
                item,
                WorkflowFingerprintJsonContext.Default.WorkflowQuestion),
            _ => throw new ArgumentException(
                $"Unsupported fingerprint component '{value.GetType().FullName}'.",
                nameof(value)),
        };
        AddJson(writer, element);
        return writer.Finish();
    }

    private static JsonElement Element<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToElement(value, typeInfo);

    private static string Component(string stored, object value)
        => string.IsNullOrWhiteSpace(stored) ? CreateComponent(value) : stored;

    private static void AddDocuments(
        CanonicalHashWriter writer,
        string label,
        IEnumerable<RawManifestDocument> documents)
    {
        RawManifestDocument[] ordered =
        [
            .. documents.OrderBy(static document => document.RepositoryPath, StringComparer.Ordinal),
        ];
        writer.Add($"{label}-count", ordered.Length);
        foreach (RawManifestDocument document in ordered)
        {
            writer.Add($"{label}-path", document.RepositoryPath);
            writer.Add($"{label}-content", document.Content.AsSpan());
        }
    }

    private static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static void AddJson(CanonicalHashWriter writer, JsonElement element)
    {
        writer.Add("json-kind", element.ValueKind.ToString());
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] properties =
                [
                    .. element.EnumerateObject().OrderBy(
                        static property => property.Name,
                        StringComparer.Ordinal),
                ];
                writer.Add("property-count", properties.Length);
                foreach (JsonProperty property in properties)
                {
                    writer.Add("property-name", property.Name);
                    AddJson(writer, property.Value);
                }
                break;
            case JsonValueKind.Array:
                JsonElement.ArrayEnumerator array = element.EnumerateArray();
                int count = element.GetArrayLength();
                writer.Add("array-count", count);
                foreach (JsonElement item in array)
                {
                    AddJson(writer, item);
                }
                break;
            case JsonValueKind.String:
                writer.Add("string", element.GetString());
                break;
            case JsonValueKind.Number:
                writer.Add("number", element.GetRawText());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.Add("boolean", element.GetBoolean() ? "true" : "false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.Add("null", (string?)null);
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON kind '{element.ValueKind}'.");
        }
    }

    private sealed class CanonicalHashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finished;

        public void Add(string label, string? value)
        {
            AddField(label);
            if (value is null)
            {
                AddLength(-1);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            AddLength(bytes.Length);
            _hash.AppendData(bytes);
        }

        public void Add(string label, int value)
            => Add(label, value.ToString(CultureInfo.InvariantCulture));

        public void Add(string label, long value)
            => Add(label, value.ToString(CultureInfo.InvariantCulture));

        public void Add(string label, ReadOnlySpan<byte> value)
        {
            AddField(label);
            AddLength(value.Length);
            _hash.AppendData(value);
        }

        public string Finish()
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _finished = true;
            return Convert.ToHexString(_hash.GetHashAndReset());
        }

        public void Dispose() => _hash.Dispose();

        private void AddField(string label)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            AddLength(bytes.Length);
            _hash.AppendData(bytes);
        }

        private void AddLength(int value)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, value);
            _hash.AppendData(length);
        }
    }
}
