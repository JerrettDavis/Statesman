using Microsoft.EntityFrameworkCore;
using Statesman.TestHelpers;
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
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestLeaseContext> factory =
            await database.CreateFactoryAsync<TestLeaseContext>(options => new TestLeaseContext(options));

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
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestLeaseContext> factory =
            await database.CreateFactoryAsync<TestLeaseContext>(options => new TestLeaseContext(options));

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
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestLeaseContext> factory =
            await database.CreateFactoryAsync<TestLeaseContext>(options => new TestLeaseContext(options));

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
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestLeaseContext> factory =
            await database.CreateFactoryAsync<TestLeaseContext>(options => new TestLeaseContext(options));

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
    public async Task RenewAsync_returns_false_once_the_lease_has_expired_even_with_nobody_racing()
    {
        // The contract decided in Phase 10: a lease is lost once its TTL lapses, whether or not
        // anyone else took it. Nobody acquires here, so a provider that only checks "row exists and
        // token matches" renews happily and this reads true -- which is the pre-Phase-10 behaviour
        // and the disagreement with Redis this test pins.
        var clock = new ManualTimeProvider();
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        TestDbContextFactory<TestLeaseContext> factory =
            await database.CreateFactoryAsync<TestLeaseContext>(options => new TestLeaseContext(options));

        var store = new EntityFrameworkStateLedgerStore<TestLeaseContext>("database", factory, clock);

        IStateLease? lease = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        Assert.True(await lease!.RenewAsync(TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(await lease.RenewAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RenewAsync_returns_false_when_the_rows_concurrency_token_no_longer_matches()
    {
        // StatesmanLedgerLease.ExpiresAt is a concurrency token, so a re-acquisition that rewrote
        // the row between this renewal's read and its save makes the UPDATE match zero rows and EF
        // Core raises DbUpdateConcurrencyException. The lease WAS lost, and the contract says that
        // is false, not an exception -- the same shape AcquireAsync already uses for
        // DbUpdateException. Deterministic decision logic: no SQLite race is reproduced, which is
        // the point.
        await using EntityFrameworkTestDatabase database = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<TwoPhaseLeaseContext> options = database.Options<TwoPhaseLeaseContext>();
        var factory = new ArmableLeaseContextFactory(options);
        await using (TwoPhaseLeaseContext schema = await factory.CreateDbContextAsync())
        {
            await schema.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TwoPhaseLeaseContext>("database", factory);
        IStateLease? lease = await store.AcquireAsync("resource", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        factory.Arm(new DbUpdateConcurrencyException("Simulated concurrent re-acquisition."));

        Assert.False(await lease!.RenewAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task AcquireAsync_returns_null_when_SaveChanges_fails_but_a_valid_lease_already_exists()
    {
        await using EntityFrameworkTestDatabase emptyDatabase = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<TwoPhaseLeaseContext> emptyOptions = emptyDatabase.Options<TwoPhaseLeaseContext>();
        await using (var schema = new TwoPhaseLeaseContext(emptyOptions, throwOnSaveChanges: null))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using EntityFrameworkTestDatabase seededDatabase = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<TwoPhaseLeaseContext> seededOptions = seededDatabase.Options<TwoPhaseLeaseContext>();
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
        await using EntityFrameworkTestDatabase emptyDatabase = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<TwoPhaseLeaseContext> emptyOptions = emptyDatabase.Options<TwoPhaseLeaseContext>();
        await using (var schema = new TwoPhaseLeaseContext(emptyOptions, throwOnSaveChanges: null))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        // The "verify" database stays empty too — a genuine provider failure unrelated to
        // another writer winning the race.
        await using EntityFrameworkTestDatabase stillEmptyDatabase = await EntityFrameworkTestDatabase.CreateAsync();
        DbContextOptions<TwoPhaseLeaseContext> stillEmptyOptions = stillEmptyDatabase.Options<TwoPhaseLeaseContext>();
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

    /// <summary>
    /// Hands out working contexts until <see cref="Arm"/> is called, then contexts whose
    /// <c>SaveChangesAsync</c> throws. That ordering is what lets one test acquire the lease for
    /// real and fail only the renewal's save.
    /// </summary>
    private sealed class ArmableLeaseContextFactory : IDbContextFactory<TwoPhaseLeaseContext>
    {
        private readonly DbContextOptions<TwoPhaseLeaseContext> _options;
        private Exception? _throwOnSaveChanges;

        public ArmableLeaseContextFactory(DbContextOptions<TwoPhaseLeaseContext> options) =>
            _options = options;

        public void Arm(Exception exception) => _throwOnSaveChanges = exception;

        public TwoPhaseLeaseContext CreateDbContext() => new(_options, _throwOnSaveChanges);

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
}
