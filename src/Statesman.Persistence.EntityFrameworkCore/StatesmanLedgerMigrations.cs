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
}
