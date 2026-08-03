using System.Net;

namespace WinMatsch.Downloads.Tests;

/// <summary>
/// A configurable fake <see cref="HttpMessageHandler"/> for tests: dispatches to a scripted
/// responder, counts requests, and tracks how many are in flight concurrently.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _requestCount;
    private int _inFlight;
    private int _maxObservedConcurrency;

    /// <param name="responder">
    /// Produces the response for a request; the second argument is the 1-based number of the
    /// request.
    /// </param>
    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>The number of requests the downloader has issued, including retries and redirect hops.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>The highest number of requests observed in flight at the same time.</summary>
    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    /// <summary>An artificial delay per request, used to force overlap in concurrency tests.</summary>
    public TimeSpan PerRequestDelay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int requestNumber = Interlocked.Increment(ref _requestCount);
        int inFlight = Interlocked.Increment(ref _inFlight);
        UpdateMaxConcurrency(inFlight);
        try
        {
            if (PerRequestDelay > TimeSpan.Zero)
            {
                await Task.Delay(PerRequestDelay, cancellationToken);
            }

            HttpResponseMessage response = _responder(request, requestNumber);
            response.RequestMessage ??= request;

            return response;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaxConcurrency(int observed)
    {
        int current;
        while (observed > (current = Volatile.Read(ref _maxObservedConcurrency)))
        {
            Interlocked.CompareExchange(ref _maxObservedConcurrency, observed, current);
        }
    }
}
