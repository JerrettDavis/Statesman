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
}
