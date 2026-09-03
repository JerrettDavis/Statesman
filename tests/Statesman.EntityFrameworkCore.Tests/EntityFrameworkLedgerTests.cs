using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Statesman.Testing;

namespace Statesman.EntityFrameworkCore.Tests;

public sealed class EntityFrameworkLedgerTests
{
    private static readonly StateKey<AccountState> Key = StateKey.Define<AccountState>("accounts/account");

    [Fact]
    public async Task Provider_preserves_head_and_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLedgerContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestContextFactory(options);
        await using (TestLedgerContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var store = new EntityFrameworkStateLedgerStore<TestLedgerContext>("database", factory);
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("ef")
            .State(Key, state => state.StoreWith("database"))
            .Build();
        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration, stores: new[] { store });
        IState<AccountState> state = harness.Runtime.State(Key);

        await state.SetAsync(new AccountState(10m));
        await state.UpdateAsync(value => value! with { Balance = value.Balance + 5m });
        var history = new List<IStateSnapshot<AccountState>>();
        await foreach (IStateSnapshot<AccountState> snapshot in state.HistoryAsync())
        {
            history.Add(snapshot);
        }

        Assert.Equal(15m, state.Current.RequiredValue.Balance);
        Assert.Equal(2, history.Count);
        Assert.Equal(new long[] { 2, 1 }, history.Select(snapshot => snapshot.Revision));
    }

    [Fact]
    public async Task Exact_import_repairs_a_divergent_cached_revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLedgerContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestContextFactory(options);
        await using (TestLedgerContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await using var store = new EntityFrameworkStateLedgerStore<TestLedgerContext>("database", factory);
        StateAddress address = new("ef-import", Key.Path, StatePartition.Default);
        StateRecord original = Record(address, "stale");
        StateRecord authoritative = original with
        {
            Payload = Encoding.UTF8.GetBytes("authoritative"),
            Source = "cold-authority",
        };

        await store.ImportAsync(original);
        await store.ImportAsync(authoritative);

        StateRecord? latest = await store.ReadLatestAsync(address);
        var history = new List<StateRecord>();
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions()))
        {
            history.Add(record);
        }

        StateRecord persisted = latest ?? throw new InvalidOperationException("The imported record was not persisted.");
        Assert.Equal(authoritative.Payload, persisted.Payload);
        Assert.Equal("cold-authority", persisted.Source);
        Assert.Single(history);
        Assert.Equal(authoritative.Payload, history[0].Payload);
    }

    [Fact]
    public async Task Service_collection_extension_registers_the_entity_framework_store()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TestLedgerContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestContextFactory(options);
        await using (TestLedgerContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("ef-di")
            .State(Key, state => state.StoreWith("database"))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestLedgerContext>>(factory);
        services.AddStatesman(
            declaration,
            builder => builder.UseEntityFrameworkStore<TestLedgerContext>("database"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        IStatesman runtime = provider.GetRequiredService<IStatesmanRegistry>().Get("ef-di");

        await runtime.State(Key).SetAsync(new AccountState(12m));
        IStateSnapshot<AccountState> snapshot = await runtime.State(Key).GetAsync(StateReadOptions.Cached);

        Assert.Equal(12m, snapshot.RequiredValue.Balance);
    }

    private static StateRecord Record(StateAddress address, string value) => new()
    {
        Address = address,
        Revision = 1,
        GlobalPosition = 1,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private sealed class TestLedgerContext : StatesmanLedgerDbContext
    {
        public TestLedgerContext(DbContextOptions<TestLedgerContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestContextFactory : IDbContextFactory<TestLedgerContext>
    {
        private readonly DbContextOptions<TestLedgerContext> _options;

        public TestContextFactory(DbContextOptions<TestLedgerContext> options)
        {
            _options = options;
        }

        public TestLedgerContext CreateDbContext() => new(_options);

        public Task<TestLedgerContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed record AccountState(decimal Balance);
}
