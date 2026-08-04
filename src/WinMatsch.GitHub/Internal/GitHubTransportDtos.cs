using System.Text.Json.Serialization;

namespace WinMatsch.GitHub.Internal;

internal sealed class RestUserDto
{
    public string Login { get; set; } = "";

    public string? Name { get; set; }

    public string? Email { get; set; }

    [JsonPropertyName("avatar_url")]
    public string AvatarUrl { get; set; } = "";
}

internal sealed class RestRepositoryDto
{
    public long Id { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = "";

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    public bool Fork { get; set; }

    public bool Private { get; set; }

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "";

    public RestRepositorySummaryDto? Parent { get; set; }

    public RestRepositoryOwnerDto Owner { get; set; } = new();

    public RestLicenseSummaryDto? License { get; set; }

    public List<string> Topics { get; set; } = [];
}

internal sealed class RestRepositorySummaryDto
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";
}

internal sealed class RestRepositoryOwnerDto
{
    public string Login { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
}

internal sealed class RestLicenseSummaryDto
{
    [JsonPropertyName("spdx_id")]
    public string? SpdxId { get; set; }
}

internal sealed class RestLicenseContentDto
{
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

internal sealed class RestContentDto
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    public string Sha { get; set; } = "";

    public long Size { get; set; }

    public string Encoding { get; set; } = "";

    public string? Content { get; set; }
}

internal sealed class RestTreeDto
{
    public bool Truncated { get; set; }

    public List<RestTreeEntryDto> Tree { get; set; } = [];
}

internal sealed class RestTreeEntryDto
{
    public string Path { get; set; } = "";

    public string Sha { get; set; } = "";

    public string Type { get; set; } = "";

    public long? Size { get; set; }
}

internal sealed class RestReleaseDto
{
    public long Id { get; set; }

    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    public string? Name { get; set; }

    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    public List<RestReleaseAssetDto> Assets { get; set; } = [];
}

internal sealed class RestReleaseAssetDto
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class RestBranchDto
{
    public string Name { get; set; } = "";

    public RestCommitSummaryDto Commit { get; set; } = new();

    public bool Protected { get; set; }
}

internal sealed class RestCommitSummaryDto
{
    public string Sha { get; set; } = "";
}

internal sealed class RestReferenceDto
{
    public string Ref { get; set; } = "";

    public RestGitObjectDto Object { get; set; } = new();
}

internal sealed class RestGitObjectDto
{
    public string Sha { get; set; } = "";
}

internal sealed class CreateReferenceDto
{
    public string Ref { get; set; } = "";

    public string Sha { get; set; } = "";
}

internal sealed class RestCompareDto
{
    public string Status { get; set; } = "";

    [JsonPropertyName("ahead_by")]
    public int AheadBy { get; set; }

    [JsonPropertyName("behind_by")]
    public int BehindBy { get; set; }

    [JsonPropertyName("total_commits")]
    public int TotalCommits { get; set; }

    [JsonPropertyName("merge_base_commit")]
    public RestCommitSummaryDto? MergeBaseCommit { get; set; }

    public List<RestComparedCommitDto> Commits { get; set; } = [];
}

internal sealed class RestComparedCommitDto
{
    public string Sha { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    public RestCommitDetailDto Commit { get; set; } = new();
}

internal sealed class RestCommitDetailDto
{
    public string Message { get; set; } = "";

    public RestTreeSummaryDto Tree { get; set; } = new();
}

internal sealed class RestTreeSummaryDto
{
    public string Sha { get; set; } = "";
}

internal sealed class CreateForkDto
{
    public string? Organization { get; set; }

    [JsonPropertyName("default_branch_only")]
    public bool DefaultBranchOnly { get; set; } = true;
}

internal sealed class MergeUpstreamDto
{
    public string Branch { get; set; } = "";
}

internal sealed class RestMergeUpstreamResultDto
{
    public string Message { get; set; } = "";

    [JsonPropertyName("merge_type")]
    public string MergeType { get; set; } = "";

    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; set; }
}

internal sealed class RestPullRequestDto
{
    public long Number { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Body { get; set; }

    public string State { get; set; } = "";

    public bool Draft { get; set; }

    public RestPullRequestRefDto Head { get; set; } = new();

    public RestPullRequestRefDto Base { get; set; } = new();

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RestIssueSearchResponseDto
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("incomplete_results")]
    public bool IncompleteResults { get; set; }

    public List<RestIssueSearchItemDto>? Items { get; set; }
}

internal sealed class RestIssueSearchItemDto
{
    public long Number { get; set; }

    [JsonPropertyName("pull_request")]
    public RestIssueSearchPullRequestDto? PullRequest { get; set; }
}

internal sealed class RestIssueSearchPullRequestDto
{
    public string? Url { get; set; }
}

internal sealed class RestPullRequestRefDto
{
    public string Label { get; set; } = "";

    public string Ref { get; set; } = "";

    public string Sha { get; set; } = "";

    public RestRepositoryDto? Repo { get; set; }

    public RestPullRequestUserDto? User { get; set; }
}

internal sealed class RestPullRequestChangedFileDto
{
    public string Filename { get; set; } = "";

    public string Status { get; set; } = "modified";

    [JsonPropertyName("previous_filename")]
    public string? PreviousFilename { get; set; }
}

internal sealed class RestPullRequestUserDto
{
    public string Login { get; set; } = "";
}

internal sealed class CreatePullRequestDto
{
    public string Title { get; set; } = "";

    public string? Body { get; set; }

    public string Head { get; set; } = "";

    public string Base { get; set; } = "";

    public bool Draft { get; set; }
}

internal sealed class UpdatePullRequestDto
{
    public string State { get; set; } = "";
}

internal sealed class CreateCommentDto
{
    public string Body { get; set; } = "";
}

internal sealed class RestCommentDto
{
    public long Id { get; set; }

    public string Body { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RestErrorDto
{
    public string? Message { get; set; }

    public List<RestErrorDetailDto>? Errors { get; set; }
}

internal sealed class RestErrorDetailDto
{
    public string? Resource { get; set; }

    public string? Field { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }
}

internal sealed class RestGitCommitDto
{
    public string Sha { get; set; } = "";

    public RestTreeSummaryDto Tree { get; set; } = new();
}

internal sealed class CreateBlobDto
{
    public string Content { get; set; } = "";

    public string Encoding { get; set; } = "base64";
}

internal sealed class CreatedBlobDto
{
    public string Sha { get; set; } = "";
}

internal sealed class CreateTreeDto
{
    [JsonPropertyName("base_tree")]
    public string BaseTree { get; set; } = "";

    public List<CreateTreeEntryDto> Tree { get; set; } = [];
}

internal sealed class CreateTreeEntryDto
{
    public string Path { get; set; } = "";

    public string Mode { get; set; } = "100644";

    public string Type { get; set; } = "blob";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Sha { get; set; }
}

internal sealed class CreatedTreeDto
{
    public string Sha { get; set; } = "";
}

internal sealed class CreateGitCommitDto
{
    public string Message { get; set; } = "";

    public string Tree { get; set; } = "";

    public List<string> Parents { get; set; } = [];
}

internal sealed class CreatedGitCommitDto
{
    public string Sha { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
}

internal sealed class UpdateReferenceDto
{
    public string Sha { get; set; } = "";

    public bool Force { get; set; }
}

internal sealed class GraphQlViewerRequestDto
{
    public string Query { get; set; } = "";
}

internal sealed class GraphQlViewerResponseDto
{
    public GraphQlViewerDataDto? Data { get; set; }

    public List<GraphQlErrorDto>? Errors { get; set; }
}

internal sealed class GraphQlViewerDataDto
{
    public GraphQlUserDto? Viewer { get; set; }

    public GraphQlRateLimitDto? RateLimit { get; set; }
}

internal sealed class GraphQlUserDto
{
    public string Login { get; set; } = "";

    public string? Name { get; set; }

    public string? Email { get; set; }

    public string AvatarUrl { get; set; } = "";
}

internal sealed class GraphQlRepositoryRequestDto
{
    public string Query { get; set; } = "";

    public GraphQlRepositoryVariablesDto Variables { get; set; } = new();
}

internal sealed class GraphQlRepositoryVariablesDto
{
    public string Owner { get; set; } = "";

    public string Name { get; set; } = "";
}

internal sealed class GraphQlRepositoryResponseDto
{
    public GraphQlRepositoryDataDto? Data { get; set; }

    public List<GraphQlErrorDto>? Errors { get; set; }
}

internal sealed class GraphQlRepositoryDataDto
{
    public GraphQlRepositoryDto? Repository { get; set; }

    public GraphQlRateLimitDto? RateLimit { get; set; }
}

internal sealed class GraphQlRepositoryDto
{
    public string Id { get; set; } = "";

    public string NameWithOwner { get; set; } = "";

    public string Url { get; set; } = "";

    public bool IsPrivate { get; set; }

    public bool IsFork { get; set; }

    public GraphQlRepositorySummaryDto? Parent { get; set; }

    public GraphQlBranchDto? DefaultBranchRef { get; set; }
}

internal sealed class GraphQlRepositorySummaryDto
{
    public string NameWithOwner { get; set; } = "";
}

internal sealed class GraphQlBranchDto
{
    public string Name { get; set; } = "";
}

internal sealed class GraphQlRateLimitDto
{
    public int Limit { get; set; }

    public int Remaining { get; set; }

    public int Used { get; set; }

    public DateTimeOffset ResetAt { get; set; }
}

internal sealed class GraphQlPullRequestFilesRequestDto
{
    public string Query { get; set; } = "";

    public GraphQlPullRequestFilesVariablesDto Variables { get; set; } = new();
}

internal sealed class GraphQlPullRequestFilesVariablesDto
{
    public List<string> Ids { get; set; } = [];
}

internal sealed class GraphQlPullRequestFilesResponseDto
{
    public GraphQlPullRequestFilesDataDto? Data { get; set; }

    public List<GraphQlErrorDto>? Errors { get; set; }
}

internal sealed class GraphQlPullRequestFilesDataDto
{
    public List<GraphQlPullRequestFileNodeDto?>? Nodes { get; set; }

    public GraphQlRateLimitDto? RateLimit { get; set; }
}

internal sealed class GraphQlPullRequestFileNodeDto
{
    public string Id { get; set; } = "";

    public long Number { get; set; }

    public string Title { get; set; } = "";

    public string State { get; set; } = "";

    public bool IsDraft { get; set; }

    public string HeadRefName { get; set; } = "";

    public string HeadRefOid { get; set; } = "";

    public string BaseRefName { get; set; } = "";

    public string BaseRefOid { get; set; } = "";

    public string Url { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public GraphQlRepositorySummaryDto? HeadRepository { get; set; }

    public GraphQlPullRequestFileConnectionDto? Files { get; set; }
}

internal sealed class GraphQlPullRequestFileConnectionDto
{
    public int TotalCount { get; set; }

    public List<GraphQlPullRequestChangedFileDto?>? Nodes { get; set; }

    public GraphQlPageInfoDto? PageInfo { get; set; }
}

internal sealed class GraphQlPullRequestChangedFileDto
{
    public string Path { get; set; } = "";

    public string ChangeType { get; set; } = "";
}

internal sealed class GraphQlPageInfoDto
{
    public bool HasNextPage { get; set; }
}

internal sealed class GraphQlErrorDto
{
    public string Message { get; set; } = "";

    public string? Type { get; set; }
}

internal sealed class GraphQlCommitRequestDto
{
    public string Query { get; set; } = "";

    public GraphQlCommitVariablesDto Variables { get; set; } = new();
}

internal sealed class GraphQlCommitVariablesDto
{
    public GraphQlCommitInputDto Input { get; set; } = new();
}

internal sealed class GraphQlCommitInputDto
{
    public GraphQlCommitBranchDto Branch { get; set; } = new();

    public GraphQlCommitMessageDto Message { get; set; } = new();

    public string ExpectedHeadOid { get; set; } = "";

    public GraphQlFileChangesDto FileChanges { get; set; } = new();

    public string ClientMutationId { get; set; } = "";
}

internal sealed class GraphQlCommitBranchDto
{
    public string RepositoryNameWithOwner { get; set; } = "";

    public string BranchName { get; set; } = "";
}

internal sealed class GraphQlCommitMessageDto
{
    public string Headline { get; set; } = "";

    public string? Body { get; set; }
}

internal sealed class GraphQlFileChangesDto
{
    public List<GraphQlFileAdditionDto> Additions { get; set; } = [];

    public List<GraphQlFileDeletionDto> Deletions { get; set; } = [];
}

internal sealed class GraphQlFileAdditionDto
{
    public string Path { get; set; } = "";

    public string Contents { get; set; } = "";
}

internal sealed class GraphQlFileDeletionDto
{
    public string Path { get; set; } = "";
}

internal sealed class GraphQlCommitResponseDto
{
    public GraphQlCommitDataDto? Data { get; set; }

    public List<GraphQlErrorDto>? Errors { get; set; }
}

internal sealed class GraphQlCommitDataDto
{
    public GraphQlCreateCommitPayloadDto? CreateCommitOnBranch { get; set; }
}

internal sealed class GraphQlCreateCommitPayloadDto
{
    public GraphQlCreatedCommitDto? Commit { get; set; }

    public string? ClientMutationId { get; set; }
}

internal sealed class GraphQlCreatedCommitDto
{
    public string Oid { get; set; } = "";

    public string Url { get; set; } = "";
}
