using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
    /// The recorded product version is read from the shipped model snapshot, not from the running
    /// Entity Framework Core assembly: the row should record the version the migration was generated
    /// with.
    /// </para>
    /// <para>
    /// Call it once, against a database that already carries the shipped schema. Calling it against an
    /// empty database would tell Entity Framework Core the tables exist when they do not.
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

        // Idempotent rather than throwing, so an application can call this unconditionally at startup.
        if ((await history.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            return false;
        }

        string productVersion = assembly.ModelSnapshot?.Model.GetProductVersion() ?? string.Empty;
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

        return true;
    }
}
