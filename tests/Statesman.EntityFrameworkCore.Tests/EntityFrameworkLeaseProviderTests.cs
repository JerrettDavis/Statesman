using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Statesman.Testing;

namespace Statesman.EntityFrameworkCore.Tests;

public sealed class EntityFrameworkLeaseProviderTests
{
    [Fact]
    public void EntityFrameworkStateLedgerStore_reports_lease_capability()
    {
        Assert.True(typeof(EntityFrameworkStateLedgerStore<>).GetInterfaces().Contains(typeof(IStateLeaseProvider)));
    }

    [Fact]
    public async Task AcquireAsync_grants_exclusive_ownership_until_release()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLeaseContext>().UseSqlite(connection).Options;
        var factory = new TestLeaseContextFactory(options);
        await using (TestLeaseContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestLeaseContext>("database", factory);

        IStateLease? first = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(first);

        IStateLease? second = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.Null(second);

        await first!.DisposeAsync();

        IStateLease? third = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_grants_the_lease_again_once_it_expires()
    {
        var clock = new ManualTimeProvider();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLeaseContext>().UseSqlite(connection).Options;
        var factory = new TestLeaseContextFactory(options);
        await using (TestLeaseContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestLeaseContext>("database", factory, clock);

        IStateLease? first = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(first);

        clock.Advance(TimeSpan.FromSeconds(31));

        IStateLease? second = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task RenewAsync_returns_false_after_the_lease_was_released()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLeaseContext>().UseSqlite(connection).Options;
        var factory = new TestLeaseContextFactory(options);
        await using (TestLeaseContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestLeaseContext>("database", factory);

        IStateLease? lease = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        await lease!.DisposeAsync();

        Assert.False(await lease.RenewAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task A_stale_holders_dispose_does_not_release_a_successors_lease()
    {
        var clock = new ManualTimeProvider();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLeaseContext>().UseSqlite(connection).Options;
        var factory = new TestLeaseContextFactory(options);
        await using (TestLeaseContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestLeaseContext>("database", factory, clock);

        IStateLease? staleHolder = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(staleHolder);

        clock.Advance(TimeSpan.FromSeconds(31));

        IStateLease? successor = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(successor);

        // The stale holder's token no longer matches the row; its dispose must be a no-op.
        await staleHolder!.DisposeAsync();

        // The successor's lease must still be held (still renewable).
        Assert.True(await successor!.RenewAsync(TimeSpan.FromSeconds(30)));

        await successor.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_returns_null_when_SaveChanges_fails_but_a_valid_lease_already_exists()
    {
        await using var emptyConnection = new SqliteConnection("Data Source=:memory:");
        await emptyConnection.OpenAsync();
        var emptyOptions = new DbContextOptionsBuilder<TwoPhaseLeaseContext>().UseSqlite(emptyConnection).Options;
        await using (var schema = new TwoPhaseLeaseContext(emptyOptions, throwOnSaveChanges: null))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using var seededConnection = new SqliteConnection("Data Source=:memory:");
        await seededConnection.OpenAsync();
        var seededOptions = new DbContextOptionsBuilder<TwoPhaseLeaseContext>().UseSqlite(seededConnection).Options;
        await using (var seeded = new TwoPhaseLeaseContext(seededOptions, throwOnSaveChanges: null))
        {
            await seeded.Database.EnsureCreatedAsync();
            seeded.StatesmanLeases.Add(new StatesmanLedgerLease
            {
                LeaseId = "racing-resource",
                Token = "rival-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
            });
            await seeded.SaveChangesAsync();
        }

        var factory = new TwoPhaseLeaseContextFactory(
            emptyOptions, seededOptions, new DbUpdateException("Simulated concurrent insert conflict."));
        var store = new EntityFrameworkStateLedgerStore<TwoPhaseLeaseContext>("database", factory);

        IStateLease? result = await store.AcquireAsync("racing-resource", TimeSpan.FromSeconds(30));

        Assert.Null(result);
    }

    [Fact]
    public async Task AcquireAsync_rethrows_when_SaveChanges_fails_and_no_valid_lease_exists()
    {
        await using var emptyConnection = new SqliteConnection("Data Source=:memory:");
        await emptyConnection.OpenAsync();
        var emptyOptions = new DbContextOptionsBuilder<TwoPhaseLeaseContext>().UseSqlite(emptyConnection).Options;
        await using (var schema = new TwoPhaseLeaseContext(emptyOptions, throwOnSaveChanges: null))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        // The "verify" database stays empty too — a genuine provider failure unrelated to
        // another writer winning the race.
        await using var stillEmptyConnection = new SqliteConnection("Data Source=:memory:");
        await stillEmptyConnection.OpenAsync();
        var stillEmptyOptions = new DbContextOptionsBuilder<TwoPhaseLeaseContext>().UseSqlite(stillEmptyConnection).Options;
        await using (var schema2 = new TwoPhaseLeaseContext(stillEmptyOptions, throwOnSaveChanges: null))
        {
            await schema2.Database.EnsureCreatedAsync();
        }

        var factory = new TwoPhaseLeaseContextFactory(
            emptyOptions, stillEmptyOptions, new DbUpdateException("Simulated genuine failure."));
        var store = new EntityFrameworkStateLedgerStore<TwoPhaseLeaseContext>("database", factory);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await store.AcquireAsync("racing-resource", TimeSpan.FromSeconds(30)));
    }

    private sealed class TwoPhaseLeaseContext : StatesmanLedgerDbContext
    {
        private readonly Exception? _throwOnSaveChanges;

        public TwoPhaseLeaseContext(DbContextOptions<TwoPhaseLeaseContext> options, Exception? throwOnSaveChanges)
            : base(options)
        {
            _throwOnSaveChanges = throwOnSaveChanges;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_throwOnSaveChanges is not null)
            {
                throw _throwOnSaveChanges;
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class TwoPhaseLeaseContextFactory : IDbContextFactory<TwoPhaseLeaseContext>
    {
        private readonly DbContextOptions<TwoPhaseLeaseContext> _firstCallOptions;
        private readonly DbContextOptions<TwoPhaseLeaseContext> _laterCallOptions;
        private readonly Exception _throwOnSaveChanges;
        private int _callCount;

        public TwoPhaseLeaseContextFactory(
            DbContextOptions<TwoPhaseLeaseContext> firstCallOptions,
            DbContextOptions<TwoPhaseLeaseContext> laterCallOptions,
            Exception throwOnSaveChanges)
        {
            _firstCallOptions = firstCallOptions;
            _laterCallOptions = laterCallOptions;
            _throwOnSaveChanges = throwOnSaveChanges;
        }

        public TwoPhaseLeaseContext CreateDbContext()
        {
            // The first context AcquireAsync creates is the "victim" (its main transaction,
            // whose SaveChangesAsync fails); every later context is AcquireAsync's own
            // post-catch verify-read, pointed at a database that already reflects whichever
            // outcome the test wants it to observe.
            int call = Interlocked.Increment(ref _callCount);
            return call == 1
                ? new TwoPhaseLeaseContext(_firstCallOptions, _throwOnSaveChanges)
                : new TwoPhaseLeaseContext(_laterCallOptions, throwOnSaveChanges: null);
        }

        public Task<TwoPhaseLeaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestLeaseContext : StatesmanLedgerDbContext
    {
        public TestLeaseContext(DbContextOptions<TestLeaseContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestLeaseContextFactory : IDbContextFactory<TestLeaseContext>
    {
        private readonly DbContextOptions<TestLeaseContext> _options;

        public TestLeaseContextFactory(DbContextOptions<TestLeaseContext> options)
        {
            _options = options;
        }

        public TestLeaseContext CreateDbContext() => new(_options);

        public Task<TestLeaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
