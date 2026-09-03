namespace Statesman.Testing;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset? initial = null)
    {
        _utcNow = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        lock (_gate)
        {
            _utcNow += duration;
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            _utcNow = value.ToUniversalTime();
        }
    }
}
