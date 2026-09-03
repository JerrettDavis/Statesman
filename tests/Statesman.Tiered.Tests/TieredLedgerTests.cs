using Microsoft.Extensions.DependencyInjection;

namespace Statesman.Tiered.Tests;

public sealed class TieredLedgerTests
{
    private static readonly StateKey<int> Count = StateKey.Define<int>("catalog/item");
    private static readonly StateAddress Address = new(
        "tiered-tests",
        Count.Path,
        StatePartition.Default);

    [Fact]
    public async Task Cold_validation_observes_external_writes_and_repairs_hot_state()
    {
        var clock = new TestTimeProvider();
        await using var hot = new InMemoryStateLedgerStore("hot", clock);
        await using var cold = new InMemoryStateLedgerStore("cold", clock);
        await using var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        StateAppendResult first = await tiered.AppendAsync(
            Address,
            StateWriteCondition.Absent,
            Commit(1));
        StateAppendResult external = await cold.AppendAsync(
            Address,
            StateWriteCondition.AtRevision(first.Record!.Revision),
            Commit(2));

        StateRecord? observed = await tiered.ReadLatestAsync(Address);
        StateRecord? repaired = await hot.ReadLatestAsync(Address);

        Assert.Equal(external.Record!.Revision, observed!.Revision);
        Assert.Equal(external.Record.Revision, repaired!.Revision);
        Assert.Equal(external.Record.GlobalPosition, repaired.GlobalPosition);
    }

    [Fact]
    public async Task Prefer_hot_is_an_explicit_eventual_consistency_tradeoff()
    {
        var clock = new TestTimeProvider();
        await using var hot = new InMemoryStateLedgerStore("hot", clock);
        await using var cold = new InMemoryStateLedgerStore("cold", clock);
        await using var tiered = new TieredStateLedgerStore(
            "tiered",
            hot,
            cold,
            new TieredStateLedgerStoreOptions { ReadMode = TieredStateReadMode.PreferHot });

        StateAppendResult first = await tiered.AppendAsync(Address, StateWriteCondition.Absent, Commit(1));
        await cold.AppendAsync(Address, StateWriteCondition.AtRevision(first.Record!.Revision), Commit(2));

        StateRecord? observed = await tiered.ReadLatestAsync(Address);

        Assert.Equal(1, observed!.Revision);
    }

    [Fact]
    public async Task Cold_validation_repairs_divergent_content_at_the_same_revision()
    {
        var clock = new TestTimeProvider();
        await using var hot = new InMemoryStateLedgerStore("hot", clock);
        await using var cold = new InMemoryStateLedgerStore("cold", clock);
        await using var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        StateRecord cached = Record(1, 1, 10);
        StateRecord authoritative = cached with
        {
            Payload = BitConverter.GetBytes(99),
            Source = "cold-authority",
        };
        await hot.ImportAsync(cached);
        await cold.ImportAsync(authoritative);

        StateRecord? observed = await tiered.ReadLatestAsync(Address);
        StateRecord? repaired = await hot.ReadLatestAsync(Address);

        Assert.Equal(authoritative.Payload, observed!.Payload);
        Assert.Equal(authoritative.Payload, repaired!.Payload);
        Assert.Equal("cold-authority", repaired.Source);
    }

    [Fact]
    public async Task Cancellation_is_never_converted_into_a_hot_cache_fallback()
    {
        var clock = new TestTimeProvider();
        await using var hot = new InMemoryStateLedgerStore("hot", clock);
        await using var cold = new InMemoryStateLedgerStore("cold", clock);
        await cold.ImportAsync(Record(1, 1, 10));
        await using var tiered = new TieredStateLedgerStore(
            "tiered",
            hot,
            cold,
            new TieredStateLedgerStoreOptions { ServeHotWhenColdUnavailable = true });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tiered.ReadLatestAsync(Address, cancellation.Token));
    }

    [Fact]
    public async Task Service_collection_extension_registers_a_tiered_store()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("tiered-di")
            .State(Count, state => state.StoreWith("tiered"))
            .Build();
        var services = new ServiceCollection();
        services.AddStatesman(
            declaration,
            builder => builder
                .UseInMemoryStore("hot")
                .UseInMemoryStore("cold")
                .UseTieredStore("tiered", "hot", "cold"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        IStatesman runtime = provider.GetRequiredService<IStatesmanRegistry>().Get("tiered-di");

        await runtime.State(Count).SetAsync(7);
        IStateSnapshot<int> snapshot = await runtime.State(Count).GetAsync(StateReadOptions.Cached);

        Assert.Equal(7, snapshot.RequiredValue);
    }

    [Fact]
    public void Tiered_store_registration_validates_store_names()
    {
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("tiered-invalid")
            .State(Count, state => state.StoreWith("tiered"))
            .Build();

        Assert.Throws<ArgumentException>(() =>
        {
            var services = new ServiceCollection();
            services.AddStatesman(
                declaration,
                builder => builder.UseTieredStore("tiered", "tiered", "cold"));
        });

        Assert.Throws<ArgumentException>(() =>
        {
            var services = new ServiceCollection();
            services.AddStatesman(
                declaration,
                builder => builder.UseTieredStore("tiered", "hot", "hot"));
        });
    }

    private static StateRecord Record(long revision, long position, int value) => new()
    {
        Address = Address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(int).FullName!,
        SchemaVersion = 1,
        Payload = BitConverter.GetBytes(value),
        Source = "test",
    };

    private static StateCommit Commit(int value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(int).FullName!,
        Payload = BitConverter.GetBytes(value),
        Source = "test",
    };

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddTicks(Interlocked.Increment(ref _ticks));
    }
}
