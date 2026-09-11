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
/// <b>Observed, not reasoned about.</b> ROADMAP 0.3 Phase 12 added live SQL Server and PostgreSQL
/// test jobs, so this store's suite runs against all three engines, and Phase 13 runs it again under
/// the provider's retrying execution strategy. It opens no transaction of its own, so
/// <c>EnableRetryOnFailure</c> has always worked here — measured at 13 of 13 green against live
/// PostgreSQL before Phase 13 changed anything. See <c>docs/providers/index.md</c>.
/// </para>
/// <para>
/// The very first write for an outbox id has no row to conditionally update against, so it falls
/// back to an insert, and that insert can fail with <see cref="DbUpdateException"/> for two different
/// reasons that must not be handled alike: a rival writer created the row first (a genuine race,
/// resolved by retrying the conditional update against the now-existing row) or the insert failed for
/// an unrelated reason such as a constraint or conversion error, in which case the row still does not
/// exist and the original exception is rethrown rather than swallowed as though the write had
/// succeeded.
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
            // Two causes land here and they need different handling. The benign one: another writer
            // created the row between the AnyAsync above and this insert, a genuine race. Retry as the
            // conditional update, which is the correct operation now that the row exists: it raises
            // the stored value if this cursor is higher and is a no-op if it is not. Same shape as the
            // ledger's own first-acquisition race handling in EntityFrameworkStateLedgerStore.
            await using TContext retry = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            int retryUpdated = await retry.StatesmanOutboxCursors
                .Where(row => row.OutboxId == outboxId && row.Position < cursor.Position)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.Position, cursor.Position),
                    cancellationToken)
                .ConfigureAwait(false);
            if (retryUpdated > 0)
            {
                return;
            }

            // Zero rows updated is ambiguous on its own: it is what a rival's higher position looks
            // like (no-op, correct), and it is also what "the row still does not exist" looks like --
            // which means the original SaveChangesAsync failure was not a race at all, and silently
            // returning here would let a constraint or conversion error pass for success. Check which
            // one this is and rethrow the original failure, preserving its stack trace, when the row
            // never landed.
            bool rowExists = await retry.StatesmanOutboxCursors
                .AsNoTracking()
                .AnyAsync(row => row.OutboxId == outboxId, cancellationToken)
                .ConfigureAwait(false);
            if (!rowExists)
            {
                throw;
            }
        }
    }
}
