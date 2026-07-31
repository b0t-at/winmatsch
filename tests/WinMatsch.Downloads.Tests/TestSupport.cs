using System.Net;

namespace WinMatsch.Downloads.Tests;

/// <summary>
/// A read-only stream that serves a fixed prefix of bytes and then throws <see cref="IOException"/>,
/// simulating a connection dropped mid-download.
/// </summary>
internal sealed class FaultyStream(byte[] prefix) : Stream
{
    private readonly byte[] _prefix = prefix;
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _prefix.Length)
        {
            throw new IOException("Simulated connection drop.");
        }

        int read = Math.Min(count, _prefix.Length - _position);
        Array.Copy(_prefix, _position, buffer, offset, read);
        _position += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="IProgress{T}"/> that records reports synchronously on the reporting thread,
/// unlike <see cref="Progress{T}"/> which posts to a synchronization context.
/// </summary>
internal sealed class ProgressCollector : IProgress<DownloadProgress>
{
    private readonly Lock _gate = new();
    private readonly List<DownloadProgress> _reports = [];

    public IReadOnlyList<DownloadProgress> Reports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (_gate)
        {
            _reports.Add(value);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private long _utcTicks = utcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        Interlocked.Add(ref _utcTicks, amount.Ticks);
    }
}

internal sealed class ClockAdvancingStream(
    byte[] payload,
    ManualTimeProvider timeProvider,
    TimeSpan readDelay) : MemoryStream(payload, writable: false)
{
    private int _advanced;

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _advanced, 1) == 0)
        {
            timeProvider.Advance(readDelay);
        }

        return base.ReadAsync(buffer, cancellationToken);
    }
}

internal sealed class CoordinatedHttpMessageHandler(
    int participants,
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly TaskCompletionSource _participantsArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private int _maxObservedConcurrency;
    private int _requestCount;

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        int inFlight = Interlocked.Increment(ref _inFlight);
        UpdateMaxConcurrency(inFlight);
        if (inFlight >= participants)
        {
            _participantsArrived.TrySetResult();
        }

        try
        {
            await _participantsArrived.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            HttpResponseMessage response = responder(request);
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

internal sealed class ConstrainedFallbackHandler : HttpMessageHandler
{
    private TrackingContent? _headContent;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public bool HeadExchangeDisposedBeforeGet { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        if (request.Method == HttpMethod.Head)
        {
            _headContent = new TrackingContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                Content = _headContent,
                RequestMessage = request,
            });
        }

        HeadExchangeDisposedBeforeGet = _headContent?.IsDisposed == true;
        if (!HeadExchangeDisposedBeforeGet)
        {
            throw new InvalidOperationException("The constrained connection is still held by the HEAD exchange.");
        }

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([7]),
            RequestMessage = request,
        };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 0, 1);
        return Task.FromResult(response);
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent()
            : base([])
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
