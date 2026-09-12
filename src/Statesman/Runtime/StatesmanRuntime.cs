using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Statesman;

internal sealed class StatesmanRuntime : IStatesman
{
    private readonly StatesmanDeclaration _declaration;
    private readonly ConcurrentDictionary<string, IStateHandleInternal> _handles = new(StringComparer.Ordinal);
    private readonly GlobalStateChangeHub _changes = new();
    private readonly ConcurrentDictionary<StatePath, IStateContainer> _containers = new();
    // The bound on how many maintenance-failure exceptions the runtime retains. Sixty-four: large
    // enough that a burst of genuinely distinct failures is still legible to a maintainer reading the
    // collection, and small enough that the retained set can never itself be the leak. Private rather
    // than a new public type per the Phase 15 controller ruling (addendum decision 47) -- the two
    // counters on StatesmanTelemetry.Meter are the public seam for this bound, not the number itself.
    private const int MaxRetainedMaintenanceFailures = 64;

    // Bounded by MaxRetainedMaintenanceFailures in ReportMaintenanceFailure below, which is the only
    // writer. Unbounded until ROADMAP 0.3 Phase 15.
    private readonly ConcurrentQueue<Exception> _maintenanceFailures = new();

    // Guards the enqueue-then-trim pair in ReportMaintenanceFailure: reading Count and then trimming
    // on the result of that read is a check-then-act that is not atomic on its own, so two concurrent
    // reporters (two partitions' prunes failing on the same runtime) can each observe the queue over
    // the bound before either dequeues, and both dequeue -- dropping the retained set below the bound
    // instead of holding it at exactly the newest MaxRetainedMaintenanceFailures. A dedicated lock
    // rather than reusing an existing one because this is a failure path: contention here is not a
    // performance concern, only correctness is. MaintenanceFailures' ToArray() read stays lock-free --
    // ConcurrentQueue enumeration is a point-in-time snapshot and does not need the invariant enforced
    // by this gate to be safe to read.
    private readonly object _maintenanceFailuresGate = new();
    private static readonly TimeSpan MaintenanceLeaseTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, byte> _degradedMaintenanceStores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private int _initialized;
    private int _disposed;

    public StatesmanRuntime(
        StatesmanDeclaration declaration,
        IServiceProvider services,
        IStateStoreResolver stores,
        IStateSerializer serializer,
        TimeProvider timeProvider)
    {
        _declaration = declaration;
        Services = services;
        Stores = stores;
        Serializer = serializer;
        TimeProvider = timeProvider;
    }

    public string Id => Manifest.Id;

    public StatesmanManifest Manifest => _declaration.Manifest;

    public bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    internal IServiceProvider Services { get; }

    internal IStateStoreResolver Stores { get; }

    internal IStateSerializer Serializer { get; }

    internal TimeProvider TimeProvider { get; }

    internal SemaphoreSlim CommitGate { get; } = new(1, 1);

    internal IReadOnlyCollection<Exception> MaintenanceFailures => _maintenanceFailures.ToArray();

    internal IReadOnlyCollection<string> DegradedMaintenanceStores => _degradedMaintenanceStores.Keys.ToArray();

    public IState<T> State<T>(StateKey<T> key, StatePartition? partition = null)
    {
        ThrowIfDisposed();
        IStateHandleInternal handle = GetHandle(new StateReference(key.Path, partition ?? StatePartition.Default));
        if (handle is not IState<T> typed)
        {
            throw new StateTypeMismatchException(key.Path, handle.ValueType, typeof(T));
        }

        return typed;
    }

    public IStateContainer Container(StatePath path)
    {
        ThrowIfDisposed();
        StateContainerManifest manifest = Manifest.Containers.FirstOrDefault(container => container.Path == path)
            ?? throw new KeyNotFoundException(
                $"Container '{path}' is not declared in Statesman root '{Id}'.");
        return _containers.GetOrAdd(path, _ => new StateContainerView(this, manifest));
    }

    public async ValueTask<IStateSnapshot> GetAsync(
        StateReference reference,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await GetHandle(reference)
            .GetUntypedAsync(options ?? StateReadOptions.Current, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IStateSnapshot> SetAsync(
        StateReference reference,
        object value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(value);
        return await GetHandle(reference)
            .SetUntypedAsync(value, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IStateSnapshot> InvalidateAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await GetHandle(reference)
            .InvalidateUntypedAsync(reason, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IStateSnapshot> ClearAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await GetHandle(reference)
            .ClearUntypedAsync(reason, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IStateSnapshot> HistoryAsync(
        StateReference reference,
        StateHistoryOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await foreach (IStateSnapshot snapshot in GetHandle(reference)
            .HistoryUntypedAsync(options ?? new StateHistoryOptions(), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    public IAsyncEnumerable<StateChange> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _changes.SubscribeAsync(
            () => _handles.Values.Select(handle => handle.CurrentUntyped).ToArray(),
            options ?? new StateObservationOptions(),
            cancellationToken);
    }

    internal IAsyncEnumerable<StateChange> ObserveAsync(
        StatePath scope,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        bool InScope(StatePath path) => scope.IsRoot || path.IsDescendantOf(scope);
        return _changes.SubscribeAsync(
            () => _handles.Values
                .Where(handle => InScope(handle.Address.Path))
                .Select(handle => handle.CurrentUntyped)
                .ToArray(),
            change => InScope(change.Current.Address.Path),
            options ?? new StateObservationOptions(),
            cancellationToken);
    }

    public async ValueTask<StateSnapshotSet> CaptureAsync(
        IEnumerable<StateReference> references,
        StateCaptureConsistency required = StateCaptureConsistency.ProcessLocal,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(references);
        StateReference[] requested = references.Distinct().ToArray();
        IStateHandleInternal[] handles = requested.Select(GetHandle).ToArray();

        if (required == StateCaptureConsistency.SnapshotDistributed)
        {
            string[] distinctStores = handles.Select(handle => handle.Manifest.Store).Distinct().ToArray();
            if (distinctStores.Length > 1)
            {
                throw new NotSupportedException(
                    $"A SnapshotDistributed capture cannot span multiple stores ({string.Join(", ", distinctStores)}) " +
                    "— each store's IDistributedCapture only guarantees atomicity within itself, so a multi-store " +
                    "request would silently return a torn read instead of one consistent point in time. Request " +
                    "ReadCommittedDistributed instead, or split the capture per store.");
            }
        }

        if (required == StateCaptureConsistency.ProcessLocal)
        {
            StateReadOptions readOptions = options ?? StateReadOptions.Current;
            foreach (IStateHandleInternal handle in handles)
            {
                await handle.GetUntypedAsync(readOptions, cancellationToken).ConfigureAwait(false);
            }

            await CommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset capturedAt = TimeProvider.GetUtcNow();
                IStateSnapshot[] snapshots = handles.Select(handle => handle.CurrentUntyped).ToArray();
                long position = snapshots.Length == 0 ? 0 : snapshots.Max(snapshot => snapshot.GlobalPosition);
                return new StateSnapshotSet(
                    Id,
                    position,
                    capturedAt,
                    snapshots.ToDictionary(snapshot => snapshot.Address));
            }
            finally
            {
                CommitGate.Release();
            }
        }

        var recordsByAddress = new Dictionary<StateAddress, StateRecord?>();
        foreach (IGrouping<string, IStateHandleInternal> group in handles.GroupBy(handle => handle.Manifest.Store))
        {
            IStateLedgerStore store = Stores.Resolve(group.Key);
            if (!store.TryGetCapability(out IDistributedCapture? capture))
            {
                throw new NotSupportedException(
                    $"Store '{group.Key}' does not implement IDistributedCapture; cannot satisfy a {required} capture.");
            }

            IReadOnlyDictionary<StateAddress, StateRecord?> groupRecords = await capture
                .CaptureAsync(group.Select(handle => handle.Address), required, cancellationToken)
                .ConfigureAwait(false);
            foreach ((StateAddress address, StateRecord? record) in groupRecords)
            {
                recordsByAddress[address] = record;
            }
        }

        DateTimeOffset distributedCapturedAt = TimeProvider.GetUtcNow();
        IStateSnapshot[] distributedSnapshots = handles.Select(handle =>
        {
            IStateRuntimeDefinition definition = _declaration.Definitions[handle.Address.Path];
            return definition.CreateSnapshot(handle.Address, recordsByAddress[handle.Address], Serializer, distributedCapturedAt);
        }).ToArray();
        long distributedPosition = distributedSnapshots.Length == 0
            ? 0
            : distributedSnapshots.Max(snapshot => snapshot.GlobalPosition);
        return new StateSnapshotSet(
            Id,
            distributedPosition,
            distributedCapturedAt,
            distributedSnapshots.ToDictionary(snapshot => snapshot.Address));
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsInitialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsInitialized)
            {
                return;
            }

            var warm = new List<Task>();
            foreach (IStateRuntimeDefinition definition in _declaration.Definitions.Values)
            {
                StateRefreshPolicy policy = definition.Manifest.Refresh;
                if (!policy.WarmOnStart)
                {
                    continue;
                }

                StatePartition[] partitions = definition.Manifest.IsPartitioned
                    ? policy.WarmPartitions.ToArray()
                    : new[] { StatePartition.Default };
                foreach (StatePartition partition in partitions)
                {
                    IStateHandleInternal handle = GetHandle(new StateReference(definition.Manifest.Path, partition));
                    warm.Add(handle.RefreshUntypedAsync(
                        new StateWriteOptions { Source = "startup" },
                        cancellationToken).AsTask());
                }
            }

            await Task.WhenAll(warm).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public ValueTask SignalAsync(StateSignal signal, CancellationToken cancellationToken = default) =>
        SignalAsync(signal, StatePath.Root, cancellationToken);

    internal async ValueTask SignalAsync(
        StateSignal signal,
        StatePath scope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.Name))
        {
            throw new ArgumentException("A signal requires a name.", nameof(signal));
        }

        var targets = new Dictionary<string, IStateHandleInternal>(StringComparer.Ordinal);
        foreach (IStateRuntimeDefinition definition in _declaration.Definitions.Values)
        {
            if ((!scope.IsRoot && !definition.Manifest.Path.IsDescendantOf(scope)) ||
                !definition.Manifest.Refresh.Signals.Contains(signal.Name))
            {
                continue;
            }

            if (signal.Partition is StatePartition partition)
            {
                if (!definition.Manifest.IsPartitioned && partition != StatePartition.Default)
                {
                    continue;
                }

                IStateHandleInternal handle = GetHandle(new StateReference(definition.Manifest.Path, partition));
                targets[handle.Address.Canonical] = handle;
                continue;
            }

            IStateHandleInternal[] active = _handles.Values
                .Where(handle => handle.Address.Path == definition.Manifest.Path)
                .ToArray();
            if (active.Length == 0 && !definition.Manifest.IsPartitioned)
            {
                active = new[] { GetHandle(new StateReference(definition.Manifest.Path)) };
            }

            foreach (IStateHandleInternal handle in active)
            {
                targets[handle.Address.Canonical] = handle;
            }
        }

        var metadata = signal.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        StateWriteOptions options = new()
        {
            Source = $"signal:{signal.Name}",
            Metadata = metadata,
        };
        await Task.WhenAll(targets.Values.Select(handle =>
            handle.RefreshUntypedAsync(options, cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public async ValueTask MaintainAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DateTimeOffset now = TimeProvider.GetUtcNow();
        IStateHandleInternal[] due = _handles.Values.Where(handle => handle.IsMaintenanceDue(now)).ToArray();

        await Task.WhenAll(due
            .GroupBy(handle => handle.Manifest.Store)
            .Select(group => MaintainStoreGroupAsync(group.Key, group.ToArray(), cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task MaintainStoreGroupAsync(
        string storeName, IStateHandleInternal[] handles, CancellationToken cancellationToken)
    {
        IStateLedgerStore store = Stores.Resolve(storeName);
        bool hasLeaseProvider = store.TryGetCapability(out IStateLeaseProvider? leases);

        await using IStateLease? lease = hasLeaseProvider
            ? await leases!.AcquireAsync($"{Id}:{storeName}:maintenance", MaintenanceLeaseTtl, cancellationToken).ConfigureAwait(false)
            : null;

        if (hasLeaseProvider)
        {
            if (lease is null)
            {
                return;
            }
        }
        else
        {
            _degradedMaintenanceStores.TryAdd(storeName, 0);
        }

        await Task.WhenAll(handles.Select(handle =>
            handle.RefreshUntypedAsync(
                new StateWriteOptions { Source = "interval" },
                cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (IStateHandleInternal handle in _handles.Values)
        {
            handle.Complete();
        }

        _changes.Complete();
        _handles.Clear();
        _containers.Clear();
        return ValueTask.CompletedTask;
    }

    internal void Publish(StateChange change) => _changes.Publish(change);

    internal void ReportMaintenanceFailure(Exception exception)
    {
        StatesmanTelemetry.MaintenanceFailuresReported.Add(1);

        // Trim AFTER the enqueue, never before: the newest failure is the most useful one and must
        // never be the one dropped. Locked because reading Count and then trimming on that read is a
        // check-then-act that is not atomic by itself: without the lock, two concurrent reporters can
        // each observe the same over-bound Count before either dequeues, and both dequeue, dropping
        // the retained set below the bound (see _maintenanceFailuresGate's comment).
        lock (_maintenanceFailuresGate)
        {
            _maintenanceFailures.Enqueue(exception);
            while (_maintenanceFailures.Count > MaxRetainedMaintenanceFailures
                   && _maintenanceFailures.TryDequeue(out _))
            {
                StatesmanTelemetry.MaintenanceFailuresDropped.Add(1);
            }
        }
    }

    internal void EnsureActive() => ThrowIfDisposed();

    private IStateHandleInternal GetHandle(StateReference reference)
    {
        if (!_declaration.Definitions.TryGetValue(reference.Path, out IStateRuntimeDefinition? definition))
        {
            throw new KeyNotFoundException(
                $"State '{reference.Path}' is not declared in Statesman root '{Id}'.");
        }

        if (!definition.Manifest.IsPartitioned && reference.Partition != StatePartition.Default)
        {
            throw new StateDeclarationException(
                $"State '{reference.Path}' is a singleton and cannot use partition '{reference.Partition}'.");
        }

        var address = new StateAddress(Id, reference.Path, reference.Partition);
        return _handles.GetOrAdd(address.Canonical, _ => definition.CreateHandle(this, address));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
