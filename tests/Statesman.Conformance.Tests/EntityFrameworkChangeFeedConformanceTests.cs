using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against the Entity Framework Core provider over a
/// database that can host two concurrent transactions.
/// </summary>
/// <remarks>
/// <see cref="EntityFrameworkTestConcurrency.ConcurrentTransactions"/> rather than the shared
/// single-connection fixture the lease suite uses: the in-flight test needs two concurrent
/// transactions, and one Microsoft.Data.Sqlite connection object cannot host them. On SQLite that
/// means a file, where the second writer blocks on <c>BEGIN IMMEDIATE</c> until the first commits,
/// which is the serialization point under test; on a server engine it is an ordinary database.
/// </remarks>
public sealed class EntityFrameworkChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync's only clock read is OccurredAt, inside the open serializable transaction and
    // after sequence.Value++ has allocated the position.
    protected override int PauseCallIndex => 1;

    protected override async ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock)
    {
        EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync(
            EntityFrameworkTestConcurrency.ConcurrentTransactions);
        TestDbContextFactory<ConformanceContext> factory =
            await database.CreateFactoryAsync<ConformanceContext>(options => new ConformanceContext(options));

        var store = new EntityFrameworkStateLedgerStore<ConformanceContext>("database", factory, clock);
        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Cleanup = () => database.DisposeAsync(),
        };
    }

    private sealed class ConformanceContext : StatesmanLedgerDbContext
    {
        public ConformanceContext(DbContextOptions<ConformanceContext> options)
            : base(options)
        {
        }
    }
}
