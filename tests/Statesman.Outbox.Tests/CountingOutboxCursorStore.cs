namespace Statesman.Outbox.Tests;

/// <summary>
/// Wraps an <see cref="IOutboxCursorStore"/> and counts reads. <c>StateChangeDispatcher</c> reads the
/// cursor exactly once per dispatch cycle -- on the lease-unavailable path and on the drain path
/// alike -- so <see cref="ReadCalls"/> counts cycles exactly. It replaces
/// <c>FakeLeaseProvider.AcquireCalls</c> as the cycle observable, which stopped counting cycles when
/// the dispatcher became a persistent leader that acquires once and holds, and it stays correct when
/// one cycle makes several feed reads.
/// </summary>
internal sealed class CountingOutboxCursorStore : IOutboxCursorStore
{
    private readonly IOutboxCursorStore _inner;
    private int _readCalls;

    public CountingOutboxCursorStore(IOutboxCursorStore? inner = null) =>
        _inner = inner ?? new InMemoryOutboxCursorStore();

    /// <summary>Dispatch cycles that have begun. Volatile because the hosted worker cycles on a pool thread while a test reads this.</summary>
    public int ReadCalls => Volatile.Read(ref _readCalls);

    public ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readCalls);
        return _inner.ReadAsync(outboxId, cancellationToken);
    }

    public ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(outboxId, cursor, cancellationToken);
}
