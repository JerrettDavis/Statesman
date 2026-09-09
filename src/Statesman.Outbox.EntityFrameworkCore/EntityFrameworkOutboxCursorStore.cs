using Microsoft.EntityFrameworkCore;

namespace Statesman.Outbox.EntityFrameworkCore;

/// <summary>
/// An <see cref="IOutboxCursorStore"/> keeping each outbox's position in one row of
/// <c>StatesmanOutboxCursors</c>, advanced by a single conditional update that never lowers it.
/// </summary>
/// <remarks>
/// <para>
/// The monotonic write is one <c>UPDATE … WHERE OutboxId = @id AND Position &lt; @new</c> issued through
/// <c>ExecuteUpdateAsync</c>, so the comparison and the assignment are one statement with no
/// read-modify-write window between them. On SQL Server the row lock makes them atomic; on SQLite the
/// statement serializes at database level, which is stronger. That gives the same monotonic-max
/// semantics as the Redis cursor store's Lua script.
/// </para>
/// <para>
/// The alternatives were each rejected for a specific reason. An optimistic concurrency token — the
/// pattern the ledger's lease row uses — needs a read, a modify, a save and a retry loop, which is the
/// right tool when a write branches on other fields and overkill for a single comparison. A
/// serializable <c>SELECT</c>-then-<c>UPDATE</c> transaction — what the ledger's <c>AppendAsync</c> and
/// <c>AcquireAsync</c> do — costs <c>BEGIN IMMEDIATE</c> on SQLite on a write that happens every
/// dispatch cycle.
/// </para>
/// <para>
/// <b>Tested against SQLite only.</b> There is no live SQL Server test job in this repository the way
/// there is for Redis, so the row-lock path above is reasoned about rather than observed. See
/// <c>docs/providers/index.md</c>.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The consumer's own subclass of <see cref="StatesmanOutboxCursorDbContext"/>, which owns the migration.</typeparam>
public sealed class EntityFrameworkOutboxCursorStore<TContext> : IOutboxCursorStore
    where TContext : StatesmanOutboxCursorDbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    /// <summary>Creates the store over a context factory. The factory is not owned and is not disposed.</summary>
    public EntityFrameworkOutboxCursorStore(IDbContextFactory<TContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public async ValueTask<StateChangeCursor?> ReadAsync(string outboxId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);

        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        long? position = await context.StatesmanOutboxCursors
            .AsNoTracking()
            .Where(row => row.OutboxId == outboxId)
            .Select(row => (long?)row.Position)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Null before the first write, per the interface contract -- and a stored zero reads as null
        // too, because StateChangeCursor forbids a zero position and no code path here writes one.
        return position is > 0 ? new StateChangeCursor(position.Value) : null;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string outboxId, StateChangeCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);

        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        int updated = await context.StatesmanOutboxCursors
            .Where(row => row.OutboxId == outboxId && row.Position < cursor.Position)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Position, cursor.Position),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }

        // Zero rows means one of two things, and they need different handling: the row exists and
        // already holds a position at or above this one -- the no-op the contract requires, which must
        // not throw -- or the row does not exist yet.
        bool exists = await context.StatesmanOutboxCursors
            .AsNoTracking()
            .AnyAsync(row => row.OutboxId == outboxId, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        context.StatesmanOutboxCursors.Add(new StatesmanOutboxCursorEntity
        {
            OutboxId = outboxId,
            Position = cursor.Position,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another writer created the row between the AnyAsync above and this insert. Retry as the
            // conditional update, which is the correct operation now that the row exists: it raises
            // the stored value if this cursor is higher and is a no-op if it is not. Same shape as the
            // ledger's own first-acquisition race handling in EntityFrameworkStateLedgerStore.
            await using TContext retry = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await retry.StatesmanOutboxCursors
                .Where(row => row.OutboxId == outboxId && row.Position < cursor.Position)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.Position, cursor.Position),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
