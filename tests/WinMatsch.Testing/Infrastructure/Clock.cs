namespace WinMatsch.Testing.Infrastructure;

public interface IClock
{
    public DateTimeOffset UtcNow { get; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class FakeClock(DateTimeOffset initialUtcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initialUtcNow;

    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        UtcNow += elapsed;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }
}
