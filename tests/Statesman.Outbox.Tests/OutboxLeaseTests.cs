namespace Statesman.Outbox.Tests;

public sealed class OutboxLeaseTests
{
    private static OutboxOptions Options(bool requireLease = true, int batchSize = 100) => new()
    {
        OutboxId = "test",
        StoreName = "leased",
        Root = "app",
        BatchSize = batchSize,
        RequireLease = requireLease,
    };

    private static async Task SeedAsync(LeasedLedgerStore store, params long[] positions)
    {
        long revision = 0;
        foreach (long position in positions)
        {
            revision++;
            await store.ImportAsync(OutboxTestRecords.Record(position, revision));
        }
    }

    [Fact]
    public async Task A_store_without_a_lease_provider_is_refused_by_name_when_a_lease_is_required()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.StoreName = "memory";

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options));

        Assert.Contains("IStateLeaseProvider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("memory", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OutboxOptions.RequireLease), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Require_lease_false_runs_unleased_and_reports_the_degradation()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1, 2);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(requireLease: false);
        options.StoreName = "memory";
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.True(dispatcher.RunningWithoutLease);
        Assert.Equal(2, result.Published);
    }

    [Fact]
    public async Task A_leased_store_reports_no_degradation_and_names_its_lease()
    {
        await using var store = new LeasedLedgerStore();
        await using var sink = new InMemoryStateChangeSink();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), Options());

        Assert.False(dispatcher.RunningWithoutLease);
        Assert.Equal("app:leased:outbox:test", dispatcher.LeaseId);
    }

    [Fact]
    public async Task A_dispatcher_that_cannot_acquire_the_lease_publishes_nothing()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());
        store.Leases.RefuseAcquire = true;

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, result.Outcome);
        Assert.Equal(0, result.Published);
        Assert.Empty(sink.Published);
        Assert.Null(await cursors.ReadAsync("test"));
        Assert.Equal("app:leased:outbox:test", store.Leases.LastRequestedLeaseId);
    }

    [Fact]
    public async Task A_second_dispatcher_publishes_nothing_while_the_first_holds_the_lease()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var firstSink = new InMemoryStateChangeSink();
        await using var secondSink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var second = new StateChangeDispatcher(store, secondSink, cursors, Options());

        // The first dispatcher's sink runs the second dispatcher from inside the protected window,
        // so the assertion is about what is true *during* the critical section, not afterwards.
        OutboxDispatchResult? nested = null;
        await using var firstSinkProbe = new CallbackSink(firstSink, async () =>
            nested = await second.DispatchOnceAsync());
        var first = new StateChangeDispatcher(store, firstSinkProbe, cursors, Options());

        OutboxDispatchResult result = await first.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Published);
        Assert.NotNull(nested);
        Assert.Equal(OutboxDispatchOutcome.LeaseUnavailable, nested!.Outcome);
        Assert.Empty(secondSink.Published);
    }

    [Fact]
    public async Task The_lease_is_released_when_the_cycle_ends()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), Options());

        await dispatcher.DispatchOnceAsync();

        Assert.Equal(0, store.Leases.HeldCount);
    }

    [Fact]
    public async Task A_lease_lost_mid_run_stops_before_the_next_batch_without_advancing_past_it()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        OutboxOptions options = Options(batchSize: 1);
        options.LeaseRenewInterval = TimeSpan.Zero;
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);
        store.Leases.RefuseRenew = true;

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.LeaseLost, result.Outcome);
        Assert.Equal(1, result.Published);
        Assert.Equal(1, store.Leases.RenewCalls);
        Assert.Equal(1, (await cursors.ReadAsync("test"))!.Value.Position);
        Assert.Equal(0, store.Leases.HeldCount);
    }

    [Fact]
    public async Task The_lease_is_renewed_between_batches_and_the_run_continues()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(batchSize: 1);
        options.LeaseRenewInterval = TimeSpan.Zero;
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Published);
        Assert.Equal(2, store.Leases.RenewCalls);
    }

    [Fact]
    public async Task Without_a_skip_limit_a_poison_batch_blocks_the_cursor_indefinitely()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2);
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());
        }

        Assert.Null(await cursors.ReadAsync("test"));
        Assert.Equal(3, sink.Attempts);
    }

    [Fact]
    public async Task A_poison_batch_is_skipped_once_the_configured_attempt_limit_is_reached()
    {
        await using var store = new LeasedLedgerStore();
        await SeedAsync(store, 1, 2);
        await using var sink = new FailingStateChangeSink(failures: int.MaxValue);
        var cursors = new InMemoryOutboxCursorStore();
        OutboxOptions options = Options();
        options.SkipPoisonAfterAttempts = 2;
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, options);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());
        OutboxDispatchResult second = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, second.Outcome);
        Assert.Equal(0, second.Published);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, (await cursors.ReadAsync("test"))!.Value.Position);
        Assert.IsType<InvalidOperationException>(dispatcher.LastSkippedError);
    }

    /// <summary>Wraps a sink and runs a callback from inside <see cref="PublishAsync"/>.</summary>
    private sealed class CallbackSink : IStateChangeSink
    {
        private readonly IStateChangeSink _inner;
        private readonly Func<Task> _duringPublish;

        public CallbackSink(IStateChangeSink inner, Func<Task> duringPublish)
        {
            _inner = inner;
            _duringPublish = duringPublish;
        }

        public string Name => _inner.Name;

        public async ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
        {
            await _duringPublish();
            await _inner.PublishAsync(batch, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
