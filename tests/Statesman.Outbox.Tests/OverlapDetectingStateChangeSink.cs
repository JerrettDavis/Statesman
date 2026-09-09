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
    private readonly TaskCompletionSource _firstPublishStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource? _hold;
    private int _inFlight;
    private int _maxInFlight;

    /// <param name="expected">How many published messages complete <see cref="Reached"/>.</param>
    /// <param name="holdFirstPublish">
    /// When true, the first publish call suspends inside the sink until <see cref="ReleasePublish"/>
    /// is called, so a test can raise hints while a dispatch cycle is provably still running.
    /// </param>
    public OverlapDetectingStateChangeSink(int expected, bool holdFirstPublish = false)
    {
        _expected = expected;
        _hold = holdFirstPublish ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
    }

    public string Name => "overlap-detecting";

    /// <summary>Completes once <c>expected</c> messages have been published.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Completes once the first publish call has entered the sink.</summary>
    public Task FirstPublishStarted => _firstPublishStarted.Task;

    /// <summary>Lets a publish held by <c>holdFirstPublish</c> proceed. A no-op otherwise.</summary>
    public void ReleasePublish() => _hold?.TrySetResult();

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
            _firstPublishStarted.TrySetResult();
            if (_hold is not null)
            {
                // Honour the worker's stopping token so a failing test cannot hang in StopAsync.
                await _hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

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
