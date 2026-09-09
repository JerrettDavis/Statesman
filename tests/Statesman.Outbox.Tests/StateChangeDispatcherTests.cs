namespace Statesman.Outbox.Tests;

public sealed class StateChangeDispatcherTests
{
    private static OutboxOptions Options(int batchSize = 100) => new()
    {
        OutboxId = "test",
        StoreName = "memory",
        BatchSize = batchSize,
        RequireLease = false,
    };

    [Fact]
    public async Task Publishes_every_record_the_feed_yields_in_position_order()
    {
        await using var store = new InMemoryStateLedgerStore();
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3);
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Published);
        Assert.Equal(1, result.Batches);
        Assert.Equal(new[] { 1L, 2L, 3L }, sink.Published.Select(message => message.GlobalPosition).ToArray());
        Assert.Equal(3, (await cursors.ReadAsync("test"))!.Value.Position);
    }

    [Fact]
    public async Task Publishes_in_batches_of_at_most_the_configured_size()
    {
        await using var store = new InMemoryStateLedgerStore();
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3, 4, 5);
        await using var sink = new BatchRecordingSink();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), Options(batchSize: 2));

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(5, result.Published);
        Assert.Equal(3, result.Batches);
        Assert.Equal(new[] { 2, 2, 1 }, sink.BatchSizes.ToArray());
    }

    [Fact]
    public async Task The_cursor_does_not_advance_when_the_sink_rejects_the_batch()
    {
        await using var store = new InMemoryStateLedgerStore();
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3);
        await using var sink = new FailingStateChangeSink(failures: 1);
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());

        Assert.Null(await cursors.ReadAsync("test"));
        Assert.IsType<InvalidOperationException>(dispatcher.LastDispatchError);
    }

    [Fact]
    public async Task The_same_batch_is_redelivered_after_a_rejection()
    {
        await using var store = new InMemoryStateLedgerStore();
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3);
        await using var sink = new FailingStateChangeSink(failures: 1);
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.DispatchOnceAsync());
        OutboxDispatchResult second = await dispatcher.DispatchOnceAsync();

        Assert.Equal(2, sink.Attempts);
        Assert.Equal(3, second.Published);
        Assert.Equal(new[] { 1L, 2L, 3L }, sink.Published.Select(message => message.GlobalPosition).ToArray());
        Assert.Equal(3, (await cursors.ReadAsync("test"))!.Value.Position);
        Assert.Null(dispatcher.LastDispatchError);
    }

    [Fact]
    public async Task Resuming_from_a_persisted_cursor_skips_already_published_records()
    {
        await using var store = new InMemoryStateLedgerStore();
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3, 4);
        var cursors = new InMemoryOutboxCursorStore();
        await cursors.WriteAsync("test", new StateChangeCursor(2));
        await using var sink = new InMemoryStateChangeSink();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(2, result.Published);
        Assert.Equal(new[] { 3L, 4L }, sink.Published.Select(message => message.GlobalPosition).ToArray());
    }

    [Fact]
    public async Task An_empty_feed_publishes_nothing_and_leaves_the_cursor_alone()
    {
        await using var store = new InMemoryStateLedgerStore();
        await using var sink = new InMemoryStateChangeSink();
        var cursors = new InMemoryOutboxCursorStore();
        var dispatcher = new StateChangeDispatcher(store, sink, cursors, Options());

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(0, result.Published);
        Assert.Equal(0, result.Batches);
        Assert.Null(result.Cursor);
        Assert.Null(await cursors.ReadAsync("test"));
    }

    [Fact]
    public async Task A_store_without_a_change_feed_is_refused_by_name()
    {
        await using var store = new MinimalLedgerStore();
        await using var sink = new InMemoryStateChangeSink();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), Options()));

        Assert.Contains("IStateChangeFeed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("minimal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_carry_the_store_name_and_the_configured_fingerprint()
    {
        await using var store = new InMemoryStateLedgerStore("primary");
        await OutboxTestRecords.SeedAsync(store, 1);
        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options();
        options.Fingerprint = "fp-abc";
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);

        await dispatcher.DispatchOnceAsync();

        StateChangeMessage message = Assert.Single(sink.Published);
        Assert.Equal("primary", message.Store);
        Assert.Equal("fp-abc", message.Fingerprint);
        Assert.Equal("primary/app::orders/basket::default#1", message.MessageId);
    }

    [Fact]
    public async Task The_dispatcher_asks_the_feed_for_BatchSize_per_read_and_pages_until_a_page_is_empty()
    {
        await using var store = new RecordingFeedLedgerStore();
        for (long position = 1; position <= 10; position++)
        {
            await store.ImportAsync(OutboxTestRecords.Record(position, revision: position));
        }

        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(batchSize: 4);
        options.StoreName = store.Name;
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        // One cycle drains the whole backlog, so paging did not turn a backlog into one page per
        // cycle -- which is why this task depends on the lease being held across cycles.
        Assert.Equal(OutboxDispatchOutcome.Completed, result.Outcome);
        Assert.Equal(10, result.Published);

        // Four reads: 4, 4, 2, then the empty one that terminates the drain. Every one asked for
        // BatchSize, never null -- a dispatcher that read the feed unbounded records a single null.
        Assert.Equal([4, 4, 4, 4], store.RequestedTakes.ToArray());
    }

    [Fact]
    public async Task A_short_page_is_not_treated_as_the_end_of_the_feed()
    {
        // The contract sentence as a test: the drain terminates on an EMPTY page, not a short one.
        // Three records at BatchSize 4 is one short page, and the drain must still read once more.
        await using var store = new RecordingFeedLedgerStore();
        for (long position = 1; position <= 3; position++)
        {
            await store.ImportAsync(OutboxTestRecords.Record(position, revision: position));
        }

        await using var sink = new InMemoryStateChangeSink();
        OutboxOptions options = Options(batchSize: 4);
        options.StoreName = store.Name;
        await using var dispatcher = new StateChangeDispatcher(
            store, sink, new InMemoryOutboxCursorStore(), options);

        Assert.Equal(3, (await dispatcher.DispatchOnceAsync()).Published);
        Assert.Equal(2, store.RequestedTakes.Count);
    }

    private sealed class BatchRecordingSink : IStateChangeSink
    {
        private readonly List<int> _batchSizes = [];

        public string Name => "batch-recording";

        public IReadOnlyList<int> BatchSizes => _batchSizes;

        public ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default)
        {
            _batchSizes.Add(batch.Count);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
