namespace Statesman.Outbox.Tests;

/// <summary>
/// Records published messages, signals once an expected count arrives, and reports the highest
/// number of publish calls that were ever in flight at once — the observable half of "a wake never
/// starts a second dispatch cycle alongside the running one".
/// </summary>
internal sealed class OverlapDetectingStateChangeSink : IStateChangeSink
{
    private readonly int _expected;
    private readonly object _gate = new();
    private readonly List<StateChangeMessage> _published = [];
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private int _maxInFlight;

    public OverlapDetectingStateChangeSink(int expected) => _expected = expected;

    public string Name => "overlap-detecting";

    /// <summary>Completes once <c>expected</c> messages have been published.</summary>
    public Task Reached => _reached.Task;

    /// <summary>The most publish calls observed in flight simultaneously. Must never exceed one.</summary>
    public int MaxConcurrentPublishes => Volatile.Read(ref _maxInFlight);

    public int PublishedCount
    {
        get
        {
            lock (_gate)
            {
                return _published.Count;
            }
        }
    }

    public async ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
    {
        int inFlight = Interlocked.Increment(ref _inFlight);
        RaiseMax(ref _maxInFlight, inFlight);
        try
        {
            // A real suspension, so two overlapping cycles would actually be observed rather than
            // serialised by a synchronous body finishing before the other one could start.
            await Task.Yield();
            lock (_gate)
            {
                _published.AddRange(batch);
                if (_published.Count >= _expected)
                {
                    _reached.TrySetResult();
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RaiseMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
