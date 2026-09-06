using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Statesman.Outbox.Tests;

public sealed class OutboxHostingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static OutboxOptions FastOptions() => new()
    {
        OutboxId = "test",
        StoreName = "memory",
        RequireLease = false,
        PollInterval = TimeSpan.FromMilliseconds(10),
        MinRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task The_worker_dispatches_on_each_tick_until_the_feed_is_drained()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1, 2, 3);
        await using var sink = new SignalingStateChangeSink(expected: 3);
        OutboxOptions options = FastOptions();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(
            dispatcher,
            options,
            NullLogger<StatesmanOutboxHostedService>.Instance,
            TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, sink.Published.Count);
    }

    [Fact]
    public async Task A_dispatch_failure_is_logged_and_the_loop_continues()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1, 2);
        await using var sink = new SignalingStateChangeSink(expected: 2, failuresFirst: 1);
        var logger = new RecordingLogger<StatesmanOutboxHostedService>();
        OutboxOptions options = FastOptions();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(dispatcher, options, logger, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, sink.Published.Count);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Running_without_a_lease_is_logged_once_at_start()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1);
        await using var sink = new SignalingStateChangeSink(expected: 1);
        var logger = new RecordingLogger<StatesmanOutboxHostedService>();
        OutboxOptions options = FastOptions();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(dispatcher, options, logger, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Wait for proof the worker's poll loop actually ran at least one tick before
            // stopping it: StartAsync only schedules ExecuteAsync, it does not run it inline,
            // so stopping immediately can cancel the worker before its body ever executes.
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Single(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("without a lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_the_worker_completes_without_a_spurious_error()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1);
        await using var sink = new SignalingStateChangeSink(expected: 1);
        var logger = new RecordingLogger<StatesmanOutboxHostedService>();
        OutboxOptions options = FastOptions();
        var dispatcher = new StateChangeDispatcher(store, sink, new InMemoryOutboxCursorStore(), options);
        var worker = new StatesmanOutboxHostedService(dispatcher, options, logger, TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Same reasoning as above: wait for the loop to actually run before stopping it.
            await sink.Reached.WaitAsync(Timeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // A cancelled PeriodicTimer wait ends the worker's task as Canceled, not
        // RanToCompletion - that is a legitimate clean stop, not a spurious error. What matters
        // is that the task did not fault and nothing was logged at Error or above.
        Assert.NotNull(worker.ExecuteTask);
        Assert.False(worker.ExecuteTask!.IsFaulted);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task AddStatesmanOutbox_registers_one_hosted_service_per_configured_outbox()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        var services = new ServiceCollection();
        services.AddSingleton<IStateStoreResolver>(new StubStoreResolver(store));
        services.AddSingleton<ILogger<StatesmanOutboxHostedService>>(NullLogger<StatesmanOutboxHostedService>.Instance);

        services.AddStatesmanOutbox(
            options =>
            {
                options.OutboxId = "first";
                options.StoreName = "memory";
                options.RequireLease = false;
            },
            static _ => new InMemoryStateChangeSink());
        services.AddStatesmanOutbox(
            options =>
            {
                options.OutboxId = "second";
                options.StoreName = "memory";
                options.RequireLease = false;
            },
            static _ => new InMemoryStateChangeSink());

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Equal(2, hosted.Length);
        Assert.All(hosted, service => Assert.IsType<StatesmanOutboxHostedService>(service));
    }

    [Fact]
    public async Task CreateDispatcher_resolves_the_named_store_and_defaults_the_cursor_store()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1);
        var services = new ServiceCollection();
        services.AddSingleton<IStateStoreResolver>(new StubStoreResolver(store));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var sink = new InMemoryStateChangeSink();
        OutboxOptions options = FastOptions();

        StateChangeDispatcher dispatcher = StatesmanOutboxExtensions.CreateDispatcher(
            provider,
            options,
            _ => sink,
            static _ => new InMemoryOutboxCursorStore());

        OutboxDispatchResult result = await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, result.Published);
        Assert.Equal("memory", Assert.Single(sink.Published).Store);
    }

    private sealed class StubStoreResolver : IStateStoreResolver
    {
        private readonly IStateLedgerStore _store;

        public StubStoreResolver(IStateLedgerStore store) => _store = store;

        public IStateLedgerStore Resolve(string name) => _store;
    }
}
