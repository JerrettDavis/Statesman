namespace Statesman.Outbox;

/// <summary>
/// Durable storage for one outbox's resume point in a store's change feed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WriteAsync"/> is <b>monotonic</b>: a write at or below the stored value must leave the
/// stored value unchanged. That is not an optimization. It is what stops a second worker — one that
/// acquired no lease, or held a lease that silently expired — from moving the cursor forward past
/// records it never published, which would be a permanent, silent loss that at-least-once delivery
/// must not permit.
/// </para>
/// <para>
/// <see cref="ReadAsync"/> returns <see langword="null"/> before the first write, never
/// <c>new StateChangeCursor(0)</c> — a cursor position of zero is illegal and its constructor
/// throws. Cursors are keyed by outbox id <em>alone</em>: positions from different stores are
/// unrelated, so an outbox id must be unique per store, and pointing one outbox id at two different
/// stores corrupts its resume point. <c>AddStatesmanOutbox</c> throws if the same outbox id is
/// registered twice in one service collection, but nothing stops two separately-constructed
/// dispatchers outside dependency injection from sharing one.
/// </para>
/// </remarks>
public interface IOutboxCursorStore
{
    /// <summary>Reads the stored resume point, or <see langword="null"/> if this outbox has never written one.</summary>
    ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default);

    /// <summary>Advances the stored resume point. A write at or below the stored value is a no-op.</summary>
    ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default);
}
