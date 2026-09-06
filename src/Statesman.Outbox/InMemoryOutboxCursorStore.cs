using System.Collections.Concurrent;

namespace Statesman.Outbox;

/// <summary>
/// A process-local <see cref="IOutboxCursorStore"/>. Correct for tests and single-process
/// development, and useless for anything else: the cursor is lost on restart, so the outbox
/// re-delivers its entire backlog. Use <see cref="FileSystemOutboxCursorStore"/> or a distributed
/// cursor store in production.
/// </summary>
public sealed class InMemoryOutboxCursorStore : IOutboxCursorStore
{
    private readonly ConcurrentDictionary<string, long> _cursors = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<StateChangeCursor?>(
            _cursors.TryGetValue(outboxId, out long position) && position > 0
                ? new StateChangeCursor(position)
                : null);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        cancellationToken.ThrowIfCancellationRequested();

        _cursors.AddOrUpdate(
            outboxId,
            cursor.Position,
            (_, stored) => Math.Max(stored, cursor.Position));
        return ValueTask.CompletedTask;
    }
}
