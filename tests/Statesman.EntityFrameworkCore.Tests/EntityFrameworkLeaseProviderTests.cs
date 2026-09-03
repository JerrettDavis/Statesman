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
