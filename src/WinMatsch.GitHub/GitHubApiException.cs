using System.Net;

namespace WinMatsch.GitHub;

public enum GitHubApiErrorKind
{
    Unknown,
    ResourceNotFound,
    Conflict,
    GraphQlUnavailable,
    ForkNotReady,
}

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
        Exception? inner = null,
        GitHubApiErrorKind errorKind = GitHubApiErrorKind.Unknown)
        : base(message, inner, statusCode)
    {
        RequestId = requestId;
        Errors = errors ?? [];
        ErrorKind = errorKind != GitHubApiErrorKind.Unknown
            ? errorKind
            : statusCode switch
            {
                HttpStatusCode.NotFound => GitHubApiErrorKind.ResourceNotFound,
                HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity
                    => GitHubApiErrorKind.Conflict,
                _ => GitHubApiErrorKind.Unknown,
            };
    }

    public string? RequestId { get; }

    public IReadOnlyList<string> Errors { get; } = [];

    public GitHubApiErrorKind ErrorKind { get; }

    public bool IsConflict => ErrorKind == GitHubApiErrorKind.Conflict;
}
