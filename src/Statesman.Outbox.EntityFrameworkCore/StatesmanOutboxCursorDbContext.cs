using Microsoft.EntityFrameworkCore;

namespace Statesman.Outbox.EntityFrameworkCore;

/// <summary>
/// The outbox cursor table, in a context of its own.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> a <c>DbSet</c> on <c>StatesmanLedgerDbContext</c>: adding one there would
/// force a migration on every existing Entity Framework Core ledger consumer, whether or not they want
/// Entity Framework Core cursor storage. A separate opt-in context means only a consumer who chooses it
/// adds a migration, for one independent table with no relationship to the ledger's.
/// Owning the migration yourself, by subclassing this context, is the supported default and is
/// unchanged. Since ROADMAP 0.3 Phase 14 there is also an opt-in alternative: the three
/// <c>Statesman.Outbox.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}</c> packages each ship one
/// generated migration for this context, applied through
/// <c>UseStatesmanOutbox&lt;Engine&gt;Migrations()</c>. Neither arrangement is imposed: a consumer who
/// takes neither package sees exactly the behaviour this package had before they existed. See
/// <c>docs/providers/entity-framework-core-migrations.md</c>.
/// </remarks>
public class StatesmanOutboxCursorDbContext : DbContext
{
    /// <summary>Creates the context over externally-configured options.</summary>
    public StatesmanOutboxCursorDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <summary>One row per outbox id.</summary>
    public DbSet<StatesmanOutboxCursorEntity> StatesmanOutboxCursors => Set<StatesmanOutboxCursorEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<StatesmanOutboxCursorEntity>(entity =>
        {
            entity.ToTable("StatesmanOutboxCursors");
            entity.HasKey(value => value.OutboxId);
            entity.Property(value => value.OutboxId).HasMaxLength(256);
        });
    }
}

/// <summary>One outbox's persisted change-feed position.</summary>
/// <remarks>
/// No concurrency token, deliberately. Monotonicity is enforced by the <c>WHERE Position &lt; @new</c>
/// predicate on a single <c>ExecuteUpdate</c> statement rather than by a read-modify-write retry loop —
/// see <c>EntityFrameworkOutboxCursorStore{TContext}.WriteAsync</c>.
/// </remarks>
public sealed class StatesmanOutboxCursorEntity
{
    /// <summary>The <c>OutboxOptions.OutboxId</c> this cursor belongs to. The primary key.</summary>
    public string OutboxId { get; set; } = string.Empty;

    /// <summary>The persisted <c>StateChangeCursor.Position</c>. Never lowered.</summary>
    public long Position { get; set; }
}
