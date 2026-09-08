namespace Statesman.Tiered.Tests;

public sealed class TieredChangeNotifierTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void TieredStateLedgerStore_reports_the_change_notifier_capability()
    {
        Assert.True(typeof(TieredStateLedgerStore).GetInterfaces().Contains(typeof(IStateChangeNotifier)));
    }

    [Fact]
    public async Task SubscribeAsync_delegates_to_cold_even_when_the_hot_store_also_has_a_notifier()
    {
        // The load-bearing test of this phase. Both tiers can notify, and the generic forwarder in
        // TryGetCapability tries HOT first -- so if this subscription came from the forwarder, the
        // hot-only append below would signal it. A hint from hot would describe a feed no consumer
        // of a tiered store ever reads, at a position lineage unrelated to the cursors ReadAsync
        // hands out. That is the IStateLedgerReplica bug shape.
        await using var hot = new InMemoryStateLedgerStore("hot");
        await using var cold = new InMemoryStateLedgerStore("cold");
        await using var tiered = new TieredStateLedgerStore("tiered", hot, cold);
        Assert.True(tiered.TryGetCapability(out IStateChangeNotifier? notifier));

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            notifier!.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();
            Task<bool> pendingTask = pending.AsTask();

            Assert.True((await hot.AppendAsync(Address("hot-only"), StateWriteCondition.Absent, Commit("hot"))).Succeeded);
            Task settled = await Task.WhenAny(pendingTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(pendingTask, settled);

            Assert.True((await cold.AppendAsync(Address("cold"), StateWriteCondition.Absent, Commit("cold"))).Succeeded);
            Assert.True(await pendingTask.WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_append_through_the_tiered_store_signals_the_subscription()
    {
        await using var hot = new InMemoryStateLedgerStore("hot");
        await using var cold = new InMemoryStateLedgerStore("cold");
        await using var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            tiered.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> pending = hints.MoveNextAsync();

            Assert.True((await tiered.AppendAsync(Address("item"), StateWriteCondition.Absent, Commit("one"))).Succeeded);

            Assert.True(await pending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await hints.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_throws_naming_the_cold_tier_when_cold_has_no_notifier()
    {
        // Discovery still succeeds -- that is what "implemented on Tiered, delegated to cold" means
        // -- and the honest answer arrives at first use. Crucially, hot HAS a notifier here, so a
        // forwarded capability would have quietly worked instead of refusing.
        await using var hot = new InMemoryStateLedgerStore("hot");
        await using var cold = new NonNotifyingStore();
        await using var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        Assert.True(tiered.TryGetCapability(out IStateChangeNotifier? notifier));
        Assert.Same(tiered, notifier);

        NotSupportedException thrown = Assert.Throws<NotSupportedException>(() => notifier!.SubscribeAsync());
        Assert.Contains("cold store", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_ends_a_live_subscription_even_when_the_tiered_store_does_not_own_its_tiers()
    {
        // ownsStores defaults to false, so tiered.DisposeAsync() below never touches hot or cold --
        // it must still end the subscription on its own, per IStateChangeNotifier.SubscribeAsync's
        // unconditional "ends when... the store is disposed" promise.
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        using var cts = new CancellationTokenSource(Timeout);
        IAsyncEnumerator<StateChangeNotification> hints =
            tiered.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = hints.MoveNextAsync();
        Task<bool> pendingTask = pending.AsTask();

        await tiered.DisposeAsync();

        bool moved = await pendingTask.WaitAsync(Timeout);
        Assert.False(moved);
        // If this fired, the 10-second CancellationTokenSource -- not the store's own disposal --
        // is what ended the wait, which is the failure this test exists to catch.
        Assert.False(cts.IsCancellationRequested);

        await hints.DisposeAsync();

        // Cold was never disposed (ownsStores: false), so it must still work exactly as before.
        Assert.True((await cold.AppendAsync(Address("after-dispose"), StateWriteCondition.Absent, Commit("after"))).Succeeded);

        IAsyncEnumerator<StateChangeNotification> coldHints =
            cold.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            ValueTask<bool> coldPending = coldHints.MoveNextAsync();
            Assert.True((await cold.AppendAsync(Address("after-dispose-2"), StateWriteCondition.Absent, Commit("after2"))).Succeeded);
            Assert.True(await coldPending.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await coldHints.DisposeAsync();
            await hot.DisposeAsync();
            await cold.DisposeAsync();
        }
    }

    private static StateAddress Address(string leaf) =>
        new StateAddress("app", $"notify/{leaf}", StatePartition.Default);

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    /// <summary>A cold store with no capabilities at all, so the tiered store has nothing to delegate to.</summary>
    private sealed class NonNotifyingStore : IStateLedgerStore
    {
        public string Name => "non-notifying";

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StateRecord?>(null);

        public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address,
            StateHistoryOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("NonNotifyingStore does not accept writes.");

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
