using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Statesman.Testing;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared lease conformance suite, run against the Entity Framework Core provider over one open
/// in-memory SQLite connection.
/// </summary>
/// <remarks>
/// In-memory rather than the file-backed database <c>EntityFrameworkChangeFeedConformanceTests</c>
/// needs: every assertion in the lease suite is sequential, so no two of this provider's serializable
/// transactions are ever open at once. That is the condition a shared single connection cannot meet,
/// and the change-feed suite's in-flight test is the one that needs it.
/// <c>EntityFrameworkLeaseProviderTests</c> already proves the shared-connection fixture works for
/// exactly these operations. Expiry is induced by fast-forwarding the <see cref="ManualTimeProvider"/>
/// the store judges expiry against, which makes it exact rather than a real wait.
/// </remarks>
public sealed class EntityFrameworkLeaseConformanceTests : LeaseConformanceTests, IDisposable
{
    private readonly ManualTimeProvider _clock = new();
    private SqliteConnection? _connection;

    protected override TimeSpan LeaseTtl => TimeSpan.FromSeconds(30);

    protected override Task ExpireAsync()
    {
        _clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    protected override async ValueTask<ConformanceStore?> CreateAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        DbContextOptions<LeaseContext> options = new DbContextOptionsBuilder<LeaseContext>()
            .UseSqlite(_connection)
            .Options;
        var factory = new LeaseContextFactory(options);
        await using (LeaseContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<LeaseContext>("database", factory, _clock);
        return new ConformanceStore
        {
            Store = store,
            Feed = store,
            Leases = store,
        };
    }

    public void Dispose()
    {
        _connection?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private sealed class LeaseContext : StatesmanLedgerDbContext
    {
        public LeaseContext(DbContextOptions<LeaseContext> options)
            : base(options)
        {
        }
    }

    private sealed class LeaseContextFactory : IDbContextFactory<LeaseContext>
    {
        private readonly DbContextOptions<LeaseContext> _options;

        public LeaseContextFactory(DbContextOptions<LeaseContext> options) => _options = options;

        public LeaseContext CreateDbContext() => new(_options);

        public Task<LeaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
