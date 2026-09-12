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
    public async Task AddStatesmanOutbox_throws_on_a_duplicate_outbox_id_in_one_service_collection()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        var services = new ServiceCollection();
        services.AddSingleton<IStateStoreResolver>(new StubStoreResolver(store));
        services.AddSingleton<ILogger<StatesmanOutboxHostedService>>(NullLogger<StatesmanOutboxHostedService>.Instance);

        services.AddStatesmanOutbox(
            options =>
            {
                options.OutboxId = "shared";
                options.StoreName = "memory";
                options.RequireLease = false;
            },
            static _ => new InMemoryStateChangeSink());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddStatesmanOutbox(
                options =>
                {
                    options.OutboxId = "shared";
                    options.StoreName = "memory";
                    options.RequireLease = false;
                },
                static _ => new InMemoryStateChangeSink()));

        Assert.Contains("shared", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OutboxOptions.OutboxId), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_disposes_the_sink_exactly_once()
    {
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1);
        var services = new ServiceCollection();
        services.AddSingleton<IStateStoreResolver>(new StubStoreResolver(store));
        services.AddSingleton<ILogger<StatesmanOutboxHostedService>>(NullLogger<StatesmanOutboxHostedService>.Instance);
        var sink = new RecordingDisposeSink();

        services.AddStatesmanOutbox(
            options =>
            {
                options.OutboxId = "test";
                options.StoreName = "memory";
                options.RequireLease = false;
            },
            _ => sink);

        ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, sink.DisposeCount);

        await provider.DisposeAsync();

        Assert.Equal(1, sink.DisposeCount);
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

    [Fact]
    public async Task A_standby_replica_logs_when_it_enters_and_leaves_standby_and_not_once_per_poll()
    {
        // Phase 7 parked this: "a standby worker ... never logs it". A deployment in which every
        // replica is standby, or in which takeover silently never happens, produced no log line at
        // all -- while both neighbouring outcomes, LeaseLost and Skipped, log.
        //
        // The third assertion is the point. Logging per cycle would make a healthy standby replica
        // emit one line per PollInterval forever, which is why this test runs many more cycles than
        // there are transitions.
        await using var store = new InMemoryStateLedgerStore("memory");
        await OutboxTestRecords.SeedAsync(store, 1, 2);
        await using var sink = new SignalingStateChangeSink(expected: 2);
        var logger = new RecordingLogger<StatesmanOutboxHostedService>();
        OutboxOptions options = FastOptions();
        options.RequireLease = true;

        // Denies the lease for the first several cycles, then grants it. The exact count does not
        // matter; what matters is that many cycles pass in each state.
        var leases = new DenyThenGrantLeaseStore(store, denials: 5);
        var dispatcher = new StateChangeDispatcher(leases, sink, new InMemoryOutboxCursorStore(), options);
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

        string[] standbyLines = logger.Entries
            .Where(entry => entry.Message.Contains("standby", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Message)
            .ToArray();

        Assert.Contains(standbyLines, line => line.Contains("could not take", StringComparison.Ordinal));
        Assert.Contains(standbyLines, line => line.Contains("took the lease", StringComparison.Ordinal));
        Assert.Equal(2, standbyLines.Length);
    }

    /// <summary>
    /// Denies the lease for the first N acquire attempts and grants it afterwards, so a worker enters
    /// standby, stays there for several cycles, and then takes over — exercising both transitions and
    /// the steady state between them.
    /// </summary>
    private sealed class DenyThenGrantLeaseStore : IStateLedgerStore, IStateLeaseProvider, IStateChangeFeed
    {
        private readonly InMemoryStateLedgerStore _inner;
        private readonly int _denials;
        private int _attempts;

        public DenyThenGrantLeaseStore(InMemoryStateLedgerStore inner, int denials)
        {
            _inner = inner;
            _denials = denials;
        }

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask<IStateLease?> AcquireAsync(
            string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IStateLease?>(
                Interlocked.Increment(ref _attempts) <= _denials ? null : new GrantedLease());

        // StateChangeDispatcher requires IStateChangeFeed (StateChangeDispatcher.cs:112) — forwarded
        // here from the start, since InMemoryStateLedgerStore backs it and the dispatcher's
        // constructor throws immediately without it, which would mask the standby behaviour this
        // test targets.
        public IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
            StateChangeCursor? from, StateChangeReadOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(from, options, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>A lease that is always held and renews successfully.</summary>
    private sealed class GrantedLease : IStateLease
    {
        public string LeaseId => "standby-test";

        public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A sink that counts how many times <see cref="DisposeAsync"/> is called.</summary>
    private sealed class RecordingDisposeSink : IStateChangeSink
    {
        public string Name => "recording-dispose";

        public int DisposeCount { get; private set; }

        public ValueTask PublishAsync(IReadOnlyList<StateChangeMessage> batch, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubStoreResolver : IStateStoreResolver
    {
        private readonly IStateLedgerStore _store;

        public StubStoreResolver(IStateLedgerStore store) => _store = store;

        public IStateLedgerStore Resolve(string name) => _store;
    }
}
