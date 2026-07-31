using System.Net;

namespace WinMatsch.GitHub;

/// <summary>An unsuccessful GitHub REST or GraphQL operation.</summary>
public sealed class GitHubApiException : HttpRequestException
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string? message)
        : base(message)
    {
    }

    public GitHubApiException(string? message, Exception? inner)
        : base(message, inner)
    {
    }

    public GitHubApiException(
        string message,
        HttpStatusCode? statusCode,
        string? requestId,
        IReadOnlyList<string>? errors = null,
        Exception? inner = null)
        : base(message, inner, statusCode)
    {
        RequestId = requestId;
        Errors = errors ?? [];
    }

    public string? RequestId { get; }

    public IReadOnlyList<string> Errors { get; } = [];

    public bool IsConflict
        => StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity;
}
