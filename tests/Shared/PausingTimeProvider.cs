namespace Statesman.TestHelpers;

/// <summary>
/// A <see cref="TimeProvider"/> that blocks its caller's thread on the <c>pauseOnCall</c>-th
/// <see cref="GetUtcNow"/> call until <see cref="Release"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// This is the only seam into a provider's allocate-then-publish window that needs no production
/// change: every provider reads the clock for <c>StateRecord.OccurredAt</c> between allocating a
/// record's global position and making the record visible to the change feed, and
/// object-initializer members are evaluated in source order, so pausing the Nth clock read pauses
/// a writer at exactly that instant.
/// </para>
/// <para>
/// Two rules keep tests using it honest. The paused append must run on a pool thread
/// (<c>Task.Run</c>) so it does not block the test. And every test must call
/// <see cref="WaitForPauseAsync"/>, which fails loudly when the pause was never entered — without
/// it, reordering an object initializer would silently turn the test vacuous.
/// </para>
/// <para>
/// It lives here rather than in <c>Statesman.Testing</c> on purpose: the shipped testing package
/// should not grow a concurrency gadget. Consuming test projects link this file rather than copying
/// it.
/// </para>
/// </remarks>
public sealed class PausingTimeProvider : TimeProvider
{
    private readonly int _pauseOnCall;

    // Never exposed as a WaitHandle, so it needs no disposal; a test that disposed it while a
    // writer was still parked in Wait() would fault that writer instead of releasing it.
    private readonly SemaphoreSlim _release = new(0, 1);
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;
    private int _released;

    /// <summary>Creates a clock that pauses on its <paramref name="pauseOnCall"/>-th read.</summary>
    /// <param name="pauseOnCall">The 1-based index of the <see cref="GetUtcNow"/> call to pause on.</param>
    public PausingTimeProvider(int pauseOnCall)
    {
        if (pauseOnCall <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pauseOnCall), "The paused call index is 1-based.");
        }

        _pauseOnCall = pauseOnCall;
    }

    /// <summary>Completes once the paused call has been entered.</summary>
    public Task Reached => _reached.Task;

    /// <summary>How many times <see cref="GetUtcNow"/> has been called.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Releases the paused caller. Idempotent, so it is safe to call from a <c>finally</c>.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release.Release();
        }
    }

    /// <summary>
    /// Waits for the pause to be entered, and throws if it never was. Call this in every test that
    /// uses this clock: a provider that stopped reading the clock inside its allocate-then-publish
    /// window would otherwise make the test pass without testing anything.
    /// </summary>
    /// <param name="timeout">How long to wait before declaring the pause unreachable.</param>
    public async Task WaitForPauseAsync(TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(_reached.Task, Task.Delay(timeout)).ConfigureAwait(false);
        // Task.WhenAny can race with its own continuation on _reached.Task: _reached was created
        // with RunContinuationsAsynchronously, so completing it does not resolve WhenAny inline --
        // it queues a ThreadPool work item, which a starved pool can lose to Task.Delay's timer
        // even though the pause really was entered. IsCompleted is a direct, lock-free read that
        // sidesteps that queue entirely.
        if (!ReferenceEquals(completed, _reached.Task) && !_reached.Task.IsCompleted)
        {
            throw new InvalidOperationException(
                $"The pausing clock was never entered: expected call #{_pauseOnCall}, observed {Calls} call(s). " +
                "The provider under test no longer reads the clock between allocating a global position and " +
                "publishing the record, so this test would have passed without exercising anything.");
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Increment(ref _calls) == _pauseOnCall)
        {
            _reached.TrySetResult();
            _release.Wait();
        }

        return DateTimeOffset.UtcNow;
    }
}
