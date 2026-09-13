using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Statesman;

internal interface IStateHandleInternal
{
    StateAddress Address { get; }

    StateDefinitionManifest Manifest { get; }

    Type ValueType { get; }

    IStateSnapshot CurrentUntyped { get; }

    ValueTask<IStateSnapshot> GetUntypedAsync(StateReadOptions options, CancellationToken cancellationToken);

    ValueTask<IStateSnapshot> SetUntypedAsync(object value, StateWriteOptions? options, CancellationToken cancellationToken);

    ValueTask<IStateSnapshot> InvalidateUntypedAsync(string? reason, StateWriteOptions? options, CancellationToken cancellationToken);

    ValueTask<IStateSnapshot> ClearUntypedAsync(string? reason, StateWriteOptions? options, CancellationToken cancellationToken);

    IAsyncEnumerable<IStateSnapshot> HistoryUntypedAsync(StateHistoryOptions options, CancellationToken cancellationToken);

    ValueTask<IStateSnapshot> RefreshUntypedAsync(StateWriteOptions? options, CancellationToken cancellationToken);

    bool MatchesSignal(StateSignal signal);

    bool IsMaintenanceDue(DateTimeOffset now);

    void Complete();
}

internal sealed class StateHandle<T> : IState<T>, IStateHandleInternal
{
    private const int MaxWriteAttempts = 8;
    private readonly StatesmanRuntime _runtime;
    private readonly StateRuntimeDefinition<T> _definition;
    private readonly IStateLedgerStore _store;
    private readonly SemaphoreSlim _hydrateGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly StateChangeHub<T> _changes = new();
    private StateSnapshot<T> _current;
    private int _hydrated;
    private long _refreshGeneration;

    public StateHandle(StatesmanRuntime runtime, StateRuntimeDefinition<T> definition, StateAddress address)
    {
        _runtime = runtime;
        _definition = definition;
        _store = runtime.Stores.Resolve(definition.Manifest.Store);
        Address = address;
        Key = new StateKey<T>(address.Path);
        Partition = address.Partition;
        _current = StateSnapshot<T>.Absent(address, runtime.TimeProvider.GetUtcNow());
    }

    public StateAddress Address { get; }

    public StateKey<T> Key { get; }

    public StatePartition Partition { get; }

    public StateDefinitionManifest Manifest => _definition.Manifest;

    public Type ValueType => typeof(T);

    public IStateSnapshot<T> Current => ObserveCurrent();

    IStateSnapshot IStateHandleInternal.CurrentUntyped => ObserveCurrent();

    public async ValueTask<IStateSnapshot<T>> GetAsync(
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        StateReadOptions readOptions = options ?? StateReadOptions.Current;
        using Activity? activity = StartActivity("read");
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (readOptions.Mode == StateReadMode.Cached)
            {
                return ObserveCurrent();
            }

            await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);
            StateSnapshot<T> current = ObserveCurrent();
            bool absent = current.Status is StateStatus.Absent or StateStatus.Cleared;
            bool needsRefresh = readOptions.Mode switch
            {
                StateReadMode.Fresh => !current.IsFresh || current.Status is StateStatus.Invalidated or StateStatus.Faulted,
                StateReadMode.RefreshIfStale => absent || current.IsStale || current.Status is StateStatus.Invalidated or StateStatus.Faulted,
                _ => (absent && Manifest.Refresh.OnFirstRead) ||
                     (Manifest.Refresh.WhenStale &&
                         (current.IsStale || current.Status is StateStatus.Invalidated or StateStatus.Faulted)),
            };

            if (needsRefresh && _definition.HasLoader)
            {
                bool background = readOptions.Mode == StateReadMode.Current &&
                    current.HasValue &&
                    Manifest.Freshness.RefreshStaleInBackground;
                if (background)
                {
                    QueueBackgroundRefresh();
                }
                else
                {
                    current = (StateSnapshot<T>)await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            bool mayServeFaultedLastKnown =
                readOptions.AllowLastKnownOnFault &&
                current.Status == StateStatus.Faulted &&
                current.HasValue;
            bool isStrictlyFresh = current.IsFresh && current.Status == StateStatus.Ready;
            if (readOptions.Mode == StateReadMode.Fresh &&
                !isStrictlyFresh &&
                !mayServeFaultedLastKnown)
            {
                throw new StateUnavailableException(current.Address, current.Status, current.Error);
            }

            StatesmanTelemetry.Reads.Add(1, StatesmanTelemetry.Tags(Address, "read"));
            return current;
        }
        finally
        {
            StatesmanTelemetry.OperationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                StatesmanTelemetry.Tags(Address, "read"));
        }
    }

    public async ValueTask<IStateSnapshot<T>> SetAsync(
        T value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        _definition.Validate(value);
        StateWriteOptions writeOptions = options ?? new StateWriteOptions();
        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            StateSnapshot<T> previous = ObserveCurrent();
            if (Manifest.WriteBehavior == StateWriteBehavior.SuppressEquivalent &&
                previous.HasValue &&
                _definition.AreEquivalent(previous.RequiredValue, value))
            {
                return previous;
            }

            AppendAttempt result = await TryCommitValueAsync(
                previous,
                value,
                StateOperation.Set,
                writeOptions,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.Snapshot;
            }

            if (writeOptions.ExpectedRevision is not null)
            {
                throw Concurrency(previous, result.Snapshot, writeOptions.ExpectedRevision);
            }
        }

        throw Concurrency(ObserveCurrent(), ObserveCurrent());
    }

    public ValueTask<IStateSnapshot<T>> UpdateAsync(
        Func<T?, T> update,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        ArgumentNullException.ThrowIfNull(update);
        return UpdateAsync(
            (value, _) => ValueTask.FromResult(update(value)),
            options,
            cancellationToken);
    }

    public async ValueTask<IStateSnapshot<T>> UpdateAsync(
        Func<T?, CancellationToken, ValueTask<T>> update,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        ArgumentNullException.ThrowIfNull(update);
        StateWriteOptions writeOptions = options ?? new StateWriteOptions();
        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            StateSnapshot<T> previous = ObserveCurrent();
            T value = await update(previous.HasValue ? previous.Value : default, cancellationToken).ConfigureAwait(false);
            _definition.Validate(value);
            if (Manifest.WriteBehavior == StateWriteBehavior.SuppressEquivalent &&
                previous.HasValue &&
                _definition.AreEquivalent(previous.RequiredValue, value))
            {
                return previous;
            }

            AppendAttempt result = await TryCommitValueAsync(
                previous,
                value,
                StateOperation.Transitioned,
                writeOptions,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.Snapshot;
            }

            if (writeOptions.ExpectedRevision is not null)
            {
                throw Concurrency(previous, result.Snapshot, writeOptions.ExpectedRevision);
            }
        }

        throw Concurrency(ObserveCurrent(), ObserveCurrent());
    }

    public async ValueTask<IStateSnapshot<T>> DispatchAsync<TCommand>(
        TCommand command,
        string? interaction = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        StateWriteOptions writeOptions = options ?? new StateWriteOptions { Source = "interaction" };
        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);
        if (!ObserveCurrent().HasValue && _definition.HasLoader)
        {
            await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            StateSnapshot<T> previous = ObserveCurrent();
            T value = await _definition.InteractAsync(
                interaction,
                previous,
                command,
                _runtime.Services,
                _runtime.TimeProvider,
                cancellationToken).ConfigureAwait(false);
            AppendAttempt result = await TryCommitValueAsync(
                previous,
                value,
                StateOperation.Transitioned,
                writeOptions,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.Snapshot;
            }

            if (writeOptions.ExpectedRevision is not null)
            {
                throw Concurrency(previous, result.Snapshot, writeOptions.ExpectedRevision);
            }
        }

        throw Concurrency(ObserveCurrent(), ObserveCurrent());
    }

    public async ValueTask<IStateSnapshot<T>> RefreshAsync(
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        if (!_definition.HasLoader)
        {
            throw new InvalidOperationException($"State '{Address}' does not declare an initial value or loader.");
        }

        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);
        long requestedGeneration = Volatile.Read(ref _refreshGeneration);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Activity? activity = null;
        long started = Stopwatch.GetTimestamp();
        bool completed = false;
        try
        {
            _runtime.EnsureActive();
            activity = StartActivity("refresh");
            if (options?.ExpectedRevision is null &&
                Volatile.Read(ref _refreshGeneration) != requestedGeneration)
            {
                return ObserveCurrent();
            }

            for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                StateSnapshot<T> previous = ObserveCurrent();
                try
                {
                    StateLoadOutcome<T> outcome = await _definition.LoadAsync(
                        previous,
                        _runtime.Services,
                        _runtime.TimeProvider,
                        cancellationToken).ConfigureAwait(false);
                    StateWriteOptions writeOptions = MergeLoadMetadata(
                        options ?? new StateWriteOptions { Source = "loader" },
                        outcome.Metadata);
                    StateOperation operation = previous.Revision == 0 && _definition.IsSeedOnly
                        ? StateOperation.Seeded
                        : StateOperation.Refreshed;
                    AppendAttempt result = await TryCommitValueAsync(
                        previous,
                        outcome.Value,
                        operation,
                        writeOptions,
                        cancellationToken,
                        outcome.Error).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        completed = true;
                        StatesmanTelemetry.Refreshes.Add(1, StatesmanTelemetry.Tags(Address, "refresh"));
                        return result.Snapshot;
                    }

                    if (writeOptions.ExpectedRevision is not null)
                    {
                        throw Concurrency(previous, result.Snapshot, writeOptions.ExpectedRevision);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not StateConcurrencyException)
                {
                    StateSnapshot<T> fault = await RecordFaultAsync(previous, exception, options, cancellationToken).ConfigureAwait(false);
                    completed = true;
                    return fault;
                }
            }

            throw Concurrency(ObserveCurrent(), ObserveCurrent());
        }
        finally
        {
            if (completed)
            {
                Interlocked.Increment(ref _refreshGeneration);
            }

            activity?.Dispose();
            _refreshGate.Release();
            StatesmanTelemetry.OperationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                StatesmanTelemetry.Tags(Address, "refresh"));
        }
    }

    public async ValueTask<IStateSnapshot<T>> InvalidateAsync(
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);
        StateWriteOptions writeOptions = WithReason(options, reason, "application");
        return await CommitMarkerAsync(StateOperation.Invalidated, StateStatus.Invalidated, keepValue: true, writeOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IStateSnapshot<T>> ClearAsync(
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        await EnsureHydratedAsync(cancellationToken).ConfigureAwait(false);
        StateWriteOptions writeOptions = WithReason(options, reason, "application");
        return await CommitMarkerAsync(StateOperation.Cleared, StateStatus.Cleared, keepValue: false, writeOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IStateSnapshot<T>> HistoryAsync(
        StateHistoryOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        StateHistoryOptions historyOptions = options ?? new StateHistoryOptions();
        historyOptions.Validate();
        await foreach (StateRecord record in _store.ReadHistoryAsync(Address, historyOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return _definition.CreateTypedSnapshot(
                Address,
                record,
                _runtime.Serializer,
                _runtime.TimeProvider.GetUtcNow());
        }
    }

    public IAsyncEnumerable<StateChange<T>> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _runtime.EnsureActive();
        return _changes.SubscribeAsync(
            () => ObserveCurrent(),
            options ?? new StateObservationOptions(),
            cancellationToken);
    }

    async ValueTask<IStateSnapshot> IStateHandleInternal.GetUntypedAsync(
        StateReadOptions options,
        CancellationToken cancellationToken) =>
        await GetAsync(options, cancellationToken).ConfigureAwait(false);

    async ValueTask<IStateSnapshot> IStateHandleInternal.SetUntypedAsync(
        object value,
        StateWriteOptions? options,
        CancellationToken cancellationToken)
    {
        if (value is not T typed)
        {
            throw new StateTypeMismatchException(Address.Path, typeof(T), value?.GetType() ?? typeof(object));
        }

        return await SetAsync(typed, options, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<IStateSnapshot> IStateHandleInternal.InvalidateUntypedAsync(
        string? reason,
        StateWriteOptions? options,
        CancellationToken cancellationToken) =>
        await InvalidateAsync(reason, options, cancellationToken).ConfigureAwait(false);

    async ValueTask<IStateSnapshot> IStateHandleInternal.ClearUntypedAsync(
        string? reason,
        StateWriteOptions? options,
        CancellationToken cancellationToken) =>
        await ClearAsync(reason, options, cancellationToken).ConfigureAwait(false);

    async IAsyncEnumerable<IStateSnapshot> IStateHandleInternal.HistoryUntypedAsync(
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (IStateSnapshot<T> snapshot in HistoryAsync(options, cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    async ValueTask<IStateSnapshot> IStateHandleInternal.RefreshUntypedAsync(
        StateWriteOptions? options,
        CancellationToken cancellationToken) =>
        await RefreshAsync(options, cancellationToken).ConfigureAwait(false);

    bool IStateHandleInternal.MatchesSignal(StateSignal signal) =>
        Manifest.Refresh.Signals.Contains(signal.Name) &&
        (signal.Partition is null || signal.Partition.Value.Equals(Partition));

    bool IStateHandleInternal.IsMaintenanceDue(DateTimeOffset now)
    {
        if (!_definition.HasLoader || Manifest.Refresh.Interval is not TimeSpan interval)
        {
            return false;
        }

        StateSnapshot<T> current = ObserveCurrent();
        return current.OccurredAt is null || SafeAdd(current.OccurredAt.Value, interval) <= now;
    }

    void IStateHandleInternal.Complete() => Complete();

    private async ValueTask EnsureHydratedAsync(CancellationToken cancellationToken)
    {
        _runtime.EnsureActive();
        if (Volatile.Read(ref _hydrated) == 1)
        {
            return;
        }

        await _hydrateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _runtime.EnsureActive();
            if (_hydrated == 1)
            {
                return;
            }

            StateRecord? record = await _store.ReadLatestAsync(Address, cancellationToken).ConfigureAwait(false);
            Volatile.Write(
                ref _current,
                _definition.CreateTypedSnapshot(Address, record, _runtime.Serializer, _runtime.TimeProvider.GetUtcNow()));
            Volatile.Write(ref _hydrated, 1);
        }
        finally
        {
            _hydrateGate.Release();
        }
    }

    private async ValueTask<AppendAttempt> TryCommitValueAsync(
        StateSnapshot<T> previous,
        T value,
        StateOperation operation,
        StateWriteOptions options,
        CancellationToken cancellationToken,
        StateError? error = null)
    {
        DateTimeOffset now = _runtime.TimeProvider.GetUtcNow();
        DateTimeOffset freshUntil = SafeAdd(now, Manifest.Freshness.FreshFor);
        DateTimeOffset serveUntil = SafeAdd(freshUntil, Manifest.Freshness.ServeStaleFor);
        var commit = new StateCommit
        {
            Operation = operation,
            Status = StateStatus.Ready,
            ValueType = Manifest.ValueType,
            SchemaVersion = Manifest.SchemaVersion,
            Payload = _runtime.Serializer.Serialize(value),
            FreshUntil = freshUntil,
            ServeUntil = serveUntil,
            Source = options.Source,
            CorrelationId = options.CorrelationId,
            CausationId = options.CausationId,
            Metadata = options.Metadata,
            Error = error,
        };
        return await TryAppendAsync(previous, commit, options.ExpectedRevision, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IStateSnapshot<T>> CommitMarkerAsync(
        StateOperation operation,
        StateStatus status,
        bool keepValue,
        StateWriteOptions options,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            StateSnapshot<T> previous = ObserveCurrent();
            byte[]? payload = keepValue && previous.HasValue
                ? _runtime.Serializer.Serialize(previous.RequiredValue)
                : null;
            DateTimeOffset freshUntil = _runtime.TimeProvider.GetUtcNow();
            var commit = new StateCommit
            {
                Operation = operation,
                Status = status,
                ValueType = Manifest.ValueType,
                SchemaVersion = Manifest.SchemaVersion,
                Payload = payload,
                FreshUntil = freshUntil,
                ServeUntil = KeepServeWindowAtOrAfter(freshUntil, previous.ServeUntil),
                Source = options.Source,
                CorrelationId = options.CorrelationId,
                CausationId = options.CausationId,
                Metadata = options.Metadata,
            };
            AppendAttempt result = await TryAppendAsync(previous, commit, options.ExpectedRevision, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.Snapshot;
            }

            if (options.ExpectedRevision is not null)
            {
                throw Concurrency(previous, result.Snapshot, options.ExpectedRevision);
            }
        }

        throw Concurrency(ObserveCurrent(), ObserveCurrent());
    }

    private async ValueTask<StateSnapshot<T>> RecordFaultAsync(
        StateSnapshot<T> previous,
        Exception exception,
        StateWriteOptions? options,
        CancellationToken cancellationToken)
    {
        StatesmanTelemetry.Faults.Add(1, StatesmanTelemetry.Tags(Address, "fault"));
        StateWriteOptions writeOptions = options ?? new StateWriteOptions { Source = "loader" };
        bool keepValue = Manifest.FaultBehavior == StateFaultBehavior.KeepLastKnown && previous.HasValue;
        DateTimeOffset freshUntil = _runtime.TimeProvider.GetUtcNow();
        var commit = new StateCommit
        {
            Operation = StateOperation.Faulted,
            Status = StateStatus.Faulted,
            ValueType = Manifest.ValueType,
            SchemaVersion = Manifest.SchemaVersion,
            Payload = keepValue ? _runtime.Serializer.Serialize(previous.RequiredValue) : null,
            FreshUntil = freshUntil,
            ServeUntil = KeepServeWindowAtOrAfter(freshUntil, previous.ServeUntil),
            Source = writeOptions.Source,
            CorrelationId = writeOptions.CorrelationId,
            CausationId = writeOptions.CausationId,
            Metadata = writeOptions.Metadata,
            Error = new StateError(
                "loader-failed",
                exception.Message,
                exception.GetType().FullName,
                IsTransient: true),
        };
        AppendAttempt result = await TryAppendAsync(previous, commit, writeOptions.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && writeOptions.ExpectedRevision is not null)
        {
            throw Concurrency(previous, result.Snapshot, writeOptions.ExpectedRevision);
        }

        return result.Snapshot;
    }

    private async ValueTask<AppendAttempt> TryAppendAsync(
        StateSnapshot<T> previous,
        StateCommit commit,
        long? explicitExpectedRevision,
        CancellationToken cancellationToken)
    {
        StateWriteCondition condition = explicitExpectedRevision is long expected
            ? StateWriteCondition.AtRevision(expected)
            : previous.Revision == 0
                ? StateWriteCondition.Absent
                : StateWriteCondition.AtRevision(previous.Revision);

        await _runtime.CommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StateAppendResult result;
        StateSnapshot<T> snapshot;
        try
        {
            _runtime.EnsureActive();
            result = await _store.AppendAsync(Address, condition, commit, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                StatesmanTelemetry.Conflicts.Add(1, StatesmanTelemetry.Tags(Address, commit.Operation.ToString()));
                snapshot = _definition.CreateTypedSnapshot(
                    Address,
                    result.Current,
                    _runtime.Serializer,
                    _runtime.TimeProvider.GetUtcNow());
                Volatile.Write(ref _current, snapshot);
                Volatile.Write(ref _hydrated, 1);
                return new AppendAttempt(false, snapshot);
            }

            snapshot = _definition.CreateTypedSnapshot(
                Address,
                result.Record,
                _runtime.Serializer,
                _runtime.TimeProvider.GetUtcNow());
            Volatile.Write(ref _current, snapshot);
            Volatile.Write(ref _hydrated, 1);
        }
        finally
        {
            _runtime.CommitGate.Release();
        }

        Publish(previous, snapshot, commit.Operation);
        StatesmanTelemetry.Commits.Add(1, StatesmanTelemetry.Tags(Address, commit.Operation.ToString()));
        try
        {
            await _store.PruneAsync(Address, Manifest.Retention, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The append is already authoritative. Cancellation only skips best-effort pruning.
        }
        catch (Exception exception)
        {
            _runtime.ReportMaintenanceFailure(_store.Name, exception);
        }

        return new AppendAttempt(true, snapshot);
    }

    private StateSnapshot<T> ObserveCurrent()
    {
        _runtime.EnsureActive();
        return _definition.Reobserve(Volatile.Read(ref _current), _runtime.TimeProvider.GetUtcNow());
    }

    private void Publish(StateSnapshot<T> previous, StateSnapshot<T> current, StateOperation operation)
    {
        var typed = new StateChange<T>(previous, current, operation);
        _changes.Publish(typed);
        _runtime.Publish(new StateChange(previous, current, operation));
    }

    private void QueueBackgroundRefresh()
    {
        Task task = RefreshAsync().AsTask();
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Activity? StartActivity(string operation)
    {
        Activity? activity = StatesmanTelemetry.ActivitySource.StartActivity($"statesman.{operation}");
        activity?.SetTag("statesman.root", Address.Root);
        activity?.SetTag("statesman.state", Address.Path.Value);
        activity?.SetTag("statesman.partition", Address.Partition.Value);
        return activity;
    }

    private StateConcurrencyException Concurrency(
        StateSnapshot<T> expected,
        StateSnapshot<T> actual,
        long? explicitExpectedRevision = null) =>
        new(
            Address,
            explicitExpectedRevision ?? (expected.Revision == 0 ? null : expected.Revision),
            actual.Revision == 0 ? null : actual.Revision);

    private static DateTimeOffset SafeAdd(DateTimeOffset value, TimeSpan duration)
    {
        if (duration == TimeSpan.MaxValue || DateTimeOffset.MaxValue - value < duration)
        {
            return DateTimeOffset.MaxValue;
        }

        return value + duration;
    }

    private static DateTimeOffset? KeepServeWindowAtOrAfter(
        DateTimeOffset freshUntil,
        DateTimeOffset? previousServeUntil) =>
        previousServeUntil is null || previousServeUntil < freshUntil
            ? freshUntil
            : previousServeUntil;

    private static StateWriteOptions MergeLoadMetadata(
        StateWriteOptions options,
        IReadOnlyDictionary<string, string> loadMetadata)
    {
        if (loadMetadata.Count == 0)
        {
            return options;
        }

        var metadata = new Dictionary<string, string>(options.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in loadMetadata)
        {
            metadata[key] = value;
        }

        return options with { Metadata = metadata };
    }

    private static StateWriteOptions WithReason(StateWriteOptions? options, string? reason, string source)
    {
        StateWriteOptions current = options ?? new StateWriteOptions { Source = source };
        if (string.IsNullOrWhiteSpace(reason))
        {
            return current;
        }

        var metadata = new Dictionary<string, string>(current.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["reason"] = reason,
        };
        return current with { Metadata = metadata };
    }

    private void Complete() => _changes.Complete();

    private readonly record struct AppendAttempt(bool Succeeded, StateSnapshot<T> Snapshot);
}
