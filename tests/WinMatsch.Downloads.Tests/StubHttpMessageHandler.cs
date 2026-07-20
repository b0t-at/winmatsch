using System.Net;

namespace WinMatsch.Downloads.Tests;

/// <summary>
/// A configurable fake <see cref="HttpMessageHandler"/> for tests: dispatches to a scripted
/// responder, counts requests, tracks how many are in flight concurrently, and mimics the real
/// handler chain's transparent redirect following (a 3xx response with a Location header is
/// re-requested and the final response's RequestMessage points at the final URL, exactly what a
/// redirect-following handler produces).
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _requestCount;
    private int _inFlight;
    private int _maxObservedConcurrency;

    /// <param name="responder">
    /// Produces the response for a request; the second argument is the 1-based number of the
    /// top-level request (redirect hops re-invoke the responder without increasing it).
    /// </param>
    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>The number of top-level requests the downloader has issued (retries count, redirect hops do not).</summary>
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

            while ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                Uri target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                response.Dispose();
                var redirected = new HttpRequestMessage(HttpMethod.Get, target);
                response = _responder(redirected, requestNumber);
                response.RequestMessage ??= redirected;
            }

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
