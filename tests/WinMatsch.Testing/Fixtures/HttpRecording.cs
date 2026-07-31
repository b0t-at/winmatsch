using System.Net;
using System.Text;
using System.Text.Json;

namespace WinMatsch.Testing.Fixtures;

public sealed record HttpInteractionRecording
{
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required Uri Uri { get; init; }

    public required int StatusCode { get; init; }

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public JsonElement? Body { get; init; }
}

public sealed class RecordedHttpMessageHandler(IEnumerable<HttpInteractionRecording> recordings)
    : HttpMessageHandler
{
    private readonly Dictionary<string, HttpInteractionRecording> _recordings =
        recordings.ToDictionary(
            recording => CreateKey(new HttpMethod(recording.Method), recording.Uri),
            StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri uri = request.RequestUri
            ?? throw new InvalidOperationException("Recorded requests require an absolute URI.");
        Requests.Add(request);

        if (!_recordings.TryGetValue(CreateKey(request.Method, uri), out HttpInteractionRecording? recording))
        {
            throw new HttpRequestException(
                $"No sanitized recording exists for {request.Method} {uri.AbsoluteUri}.");
        }

        var response = new HttpResponseMessage((HttpStatusCode)recording.StatusCode)
        {
            RequestMessage = request,
        };

        if (recording.Body is { } body)
        {
            response.Content = new StringContent(
                body.GetRawText(),
                Encoding.UTF8,
                "application/json");
        }

        foreach ((string name, string value) in recording.ResponseHeaders)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content ??= new ByteArrayContent([]);
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return Task.FromResult(response);
    }

    private static string CreateKey(HttpMethod method, Uri uri) =>
        $"{method.Method.ToUpperInvariant()} {uri.AbsoluteUri}";
}
