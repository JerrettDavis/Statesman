using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Statesman;

/// <summary>
/// The seam a shipped <c>StatesmanLedgerDbContext</c> migration is applied through.
/// </summary>
/// <remarks>
/// Shipped migrations are <b>opt-in</b>. A consumer who never calls one of the
/// <c>UseStatesmanLedger…Migrations()</c> methods sees exactly the behaviour this package had before
/// they existed: no migration is discovered, none is applied, and owning the migration yourself stays
/// the supported default. ROADMAP 0.3 Phase 14, addendum decision 28.
/// </remarks>
public static class StatesmanLedgerMigrations
{
    /// <summary>
    /// The migration-history table the shipped ledger migrations record themselves in.
    /// </summary>
    /// <remarks>
    /// Not the default <c>__EFMigrationsHistory</c>, which is shared per database: a consumer running
    /// their own migrations beside a shipped set in one database would otherwise get the two
    /// interleaved. The outbox context uses a second name of its own for the same reason, because the
    /// two contexts are independently adoptable. Addendum decision 25.
    /// </remarks>
    public const string HistoryTableName = "__StatesmanLedgerMigrationsHistory";

    /// <summary>
    /// Points this context at a shipped ledger migration assembly, after a relational provider has
    /// been selected.
    /// </summary>
    /// <param name="builder">The options builder, with <c>UseSqlite</c>/<c>UseSqlServer</c>/<c>UseNpgsql</c> already called.</param>
    /// <param name="migrationsAssembly">The assembly holding the generated migrations.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No relational provider has been selected yet.</exception>
    public static DbContextOptionsBuilder UseStatesmanLedgerMigrations(
        this DbContextOptionsBuilder builder,
        Assembly migrationsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(migrationsAssembly);

        // Read the provider's own options extension back off the builder and re-add it with the
        // migration settings applied, rather than taking them on the provider's inner options builder.
        // The inner form is the Entity Framework Core idiom, but its generic parameter binds to the
        // provider's INTERNAL options extension type, so every call site compiles with EF1001 --
        // measured, and it would spread a suppression for a second internal type across all six
        // migration packages. Addendum decision 29.
        RelationalOptionsExtension relational =
            builder.Options.Extensions.OfType<RelationalOptionsExtension>().LastOrDefault()
            ?? throw new InvalidOperationException(
                "Select a relational provider (UseSqlite, UseSqlServer or UseNpgsql) before calling "
                + nameof(UseStatesmanLedgerMigrations) + ".");

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            relational
                .WithMigrationsAssembly(migrationsAssembly)
                .WithMigrationsHistoryTableName(HistoryTableName));

        // The replacement is what makes a shipped migration reach a consumer's subclass at all.
        // Without it, MigrationsAssembly above is configured correctly and applies nothing --
        // silently. See StatesmanMigrationsAssembly for the mechanism.
#pragma warning disable EF1001
        return builder.ReplaceService<IMigrationsAssembly, StatesmanMigrationsAssembly>();
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
    /// <param name="context">A context configured by <see cref="UseStatesmanLedgerMigrations"/>.</param>
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
                "No shipped ledger migration was discovered for this context. Call "
                + nameof(UseStatesmanLedgerMigrations)
                + " with a Statesman ledger migrations assembly before baselining.");
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
