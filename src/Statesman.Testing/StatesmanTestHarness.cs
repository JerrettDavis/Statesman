namespace Statesman.Testing;

public sealed class StatesmanTestHarness : IAsyncDisposable
{
    private readonly StateStoreResolver _resolver;

    private StatesmanTestHarness(
        IStatesman runtime,
        StateStoreResolver resolver,
        TestServiceProvider services,
        ManualTimeProvider time)
    {
        Runtime = runtime;
        _resolver = resolver;
        Services = services;
        Time = time;
    }

    public IStatesman Runtime { get; }

    public TestServiceProvider Services { get; }

    public ManualTimeProvider Time { get; }

    /// <summary>Starts a fluent, strongly typed state seed for this isolated test runtime.</summary>
    public StateSeedBuilder Seed() => Runtime.Seed();

    public ValueTask ApplyFixtureAsync(
        StateFixture fixture,
        StateFixtureApplyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Runtime.ApplyFixtureAsync(fixture, options, cancellationToken: cancellationToken);

    public static StatesmanTestHarness Create(
        StatesmanDeclaration declaration,
        Action<TestServiceProvider>? services = null,
        IEnumerable<IStateLedgerStore>? stores = null,
        ManualTimeProvider? time = null)
    {
        var clock = time ?? new ManualTimeProvider();
        var provider = new TestServiceProvider().Add<TimeProvider>(clock);
        services?.Invoke(provider);
        IStateLedgerStore[] ledgers = stores?.ToArray() ?? new IStateLedgerStore[]
        {
            new InMemoryStateLedgerStore("memory", clock),
        };
        var resolver = new StateStoreResolver(ledgers, ownsStores: true);
        IStatesman runtime = declaration.CreateRuntime(provider, resolver, new JsonStateSerializer(), clock);
        return new StatesmanTestHarness(runtime, resolver, provider, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync().ConfigureAwait(false);
        await _resolver.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StateChange<T>>> CollectChangesAsync<T>(
        IState<T> state,
        Func<ValueTask> act,
        int expected,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<StateChange<T>>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using StateSubscription subscription = state.OnChange((change, _) =>
        {
            changes.Add(change);
            if (changes.Count >= expected)
            {
                observed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }, new StateObservationOptions { IncludeCurrent = false }, cancellationToken);

        await Task.Yield();
        await act().ConfigureAwait(false);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return changes;
    }

    public static async ValueTask EventuallyAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= expires)
            {
                throw new TimeoutException("The Statesman test condition did not become true before the timeout.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }
}
