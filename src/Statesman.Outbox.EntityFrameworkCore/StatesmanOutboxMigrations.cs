using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Statesman.Outbox.EntityFrameworkCore;

/// <summary>
/// The seam a shipped <c>StatesmanOutboxCursorDbContext</c> migration is applied through.
/// </summary>
/// <remarks>
/// Shipped migrations are <b>opt-in</b>. A consumer who never calls one of the
/// <c>UseStatesmanOutbox…Migrations()</c> methods sees exactly the behaviour this package had before
/// they existed. ROADMAP 0.3 Phase 14, addendum decision 28.
/// </remarks>
public static class StatesmanOutboxMigrations
{
    /// <summary>
    /// The migration-history table the shipped outbox migrations record themselves in.
    /// </summary>
    /// <remarks>
    /// A second name beside the ledger's, not a shared one: the two contexts are independently
    /// adoptable and a consumer may point both at one database. Addendum decision 25.
    /// </remarks>
    public const string HistoryTableName = "__StatesmanOutboxMigrationsHistory";

    /// <summary>
    /// Points this context at a shipped outbox migration assembly, after a relational provider has
    /// been selected.
    /// </summary>
    /// <param name="builder">The options builder, with <c>UseSqlite</c>/<c>UseSqlServer</c>/<c>UseNpgsql</c> already called.</param>
    /// <param name="migrationsAssembly">The assembly holding the generated migrations.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanOutboxMigrations(
        this DbContextOptionsBuilder builder,
        Assembly migrationsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(migrationsAssembly);

        // See StatesmanLedgerMigrations.UseStatesmanLedgerMigrations for why this reads the extension
        // off the outer builder rather than taking the provider's inner options builder. Addendum
        // decision 29.
        RelationalOptionsExtension relational =
            builder.Options.Extensions.OfType<RelationalOptionsExtension>().LastOrDefault()
            ?? throw new InvalidOperationException(
                "Select a relational provider (UseSqlite, UseSqlServer or UseNpgsql) before calling "
                + nameof(UseStatesmanOutboxMigrations) + ".");

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            relational
                .WithMigrationsAssembly(migrationsAssembly)
                .WithMigrationsHistoryTableName(HistoryTableName));

        // The replacement is what makes a shipped migration reach a consumer's subclass at all.
        // Without it, MigrationsAssembly above is configured correctly and applies nothing --
        // silently. See StatesmanOutboxMigrationsAssembly for the mechanism.
#pragma warning disable EF1001
        return builder.ReplaceService<IMigrationsAssembly, StatesmanOutboxMigrationsAssembly>();
#pragma warning restore EF1001
    }

    /// <summary>
    /// Records the shipped migrations as already applied, for a database whose schema was created by
    /// <c>EnsureCreated</c> before shipped migrations existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EnsureCreated</c> writes no migration-history row, so the first <c>Migrate()</c> against
    /// such a database fails with "table already exists". This writes the history table and one row
    /// per shipped migration id and authors no SQL of its own: every statement comes from
    /// <see cref="IHistoryRepository"/>, so it is correct on every relational provider and honours
    /// <see cref="HistoryTableName"/> automatically.
    /// </para>
    /// <para>
    /// The recorded product version is <see cref="ProductInfo.GetVersion"/> -- the running Entity
    /// Framework Core assembly's version -- the same source <c>Migrator.ApplyMigration</c> uses when
    /// it inserts a history row for a migration applied through <c>Migrate()</c>, so a baselined row
    /// is indistinguishable from one <c>Migrate()</c> would have written itself.
    /// </para>
    /// <para>
    /// Call it once, against a database that already carries the shipped schema. Calling it against an
    /// empty database would tell Entity Framework Core the tables exist when they do not.
    /// </para>
    /// <para>
    /// The create-if-not-exists statement and every insert run in one transaction, itself run through
    /// <see cref="IExecutionStrategy"/>: without the transaction, a crash between two inserts leaves a
    /// history table with SOME but not all migrations recorded, and the idempotency check above reads
    /// that as fully baselined because it only asks whether the count is nonzero. The strategy wrapper
    /// is what makes that transaction retriable at all: <c>ExecuteSqlRawAsync</c> bypasses Entity
    /// Framework Core's own per-operation retry (unlike <c>SaveChangesAsync</c> and LINQ queries), so
    /// under <c>EnableRetryOnFailure</c> a transient failure here would otherwise propagate no matter
    /// how the caller configured retries. It also keeps this method safe to call from inside another
    /// caller's own <c>strategy.ExecuteAsync</c> delegate, and matches the shape
    /// <c>EntityFrameworkStateLedgerStore</c> already uses for its own transactions. The
    /// already-baselined check is re-run INSIDE the delegate rather than once before it, so a retried
    /// attempt re-reads the history table the retry itself may have changed, rather than trusting a
    /// value read before the retry began.
    /// </para>
    /// </remarks>
    /// <param name="context">A context configured by <see cref="UseStatesmanOutboxMigrations"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when rows were written; <see langword="false"/> when the history already held migrations.</returns>
    /// <exception cref="InvalidOperationException">No shipped migration was discovered for this context.</exception>
    public static async Task<bool> BaselineAsync(
        DbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IHistoryRepository history = context.GetService<IHistoryRepository>();
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();

        // Loud, not quiet. An empty migration set here means the options are misconfigured, and
        // writing an empty history table would report success for a database nothing can migrate.
        // Addendum decision 32.
        if (assembly.Migrations.Count == 0)
        {
            throw new InvalidOperationException(
                "No shipped outbox migration was discovered for this context. Call "
                + nameof(UseStatesmanOutboxMigrations)
                + " with a Statesman outbox migrations assembly before baselining.");
        }

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct => await BaselineAttemptAsync(context, history, assembly, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    // One attempt of BaselineAsync, as the execution strategy's delegate. Addendum decision 34.
    private static async Task<bool> BaselineAttemptAsync(
        DbContext context,
        IHistoryRepository history,
        IMigrationsAssembly assembly,
        CancellationToken cancellationToken)
    {
        // Idempotent rather than throwing, so an application can call this unconditionally at startup.
        // Read INSIDE the delegate: a retried attempt must see whatever the previous, rolled-back
        // attempt left behind, not a value cached from before the retry.
        if ((await history.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return false;
        }

        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        string productVersion = ProductInfo.GetVersion();
        await context.Database
            .ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken)
            .ConfigureAwait(false);
        foreach (string migrationId in assembly.Migrations.Keys)
        {
            await context.Database
                .ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(migrationId, productVersion)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
