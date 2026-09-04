namespace Statesman;

public sealed record StateSignal(
    string Name,
    StatePartition? Partition = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record StateLoadContext(
    IServiceProvider Services,
    StateAddress Address,
    IStateSnapshot Current,
    TimeProvider TimeProvider);

public sealed record StateInteractionContext<TState, TCommand>(
    IServiceProvider Services,
    StateAddress Address,
    IStateSnapshot<TState> Current,
    TCommand Command,
    TimeProvider TimeProvider);

public interface IState<T>
{
    StateAddress Address { get; }

    StateKey<T> Key { get; }

    StatePartition Partition { get; }

    StateDefinitionManifest Manifest { get; }

    IStateSnapshot<T> Current { get; }

    ValueTask<IStateSnapshot<T>> GetAsync(
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> SetAsync(
        T value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> UpdateAsync(
        Func<T?, T> update,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> UpdateAsync(
        Func<T?, CancellationToken, ValueTask<T>> update,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> DispatchAsync<TCommand>(
        TCommand command,
        string? interaction = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> RefreshAsync(
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> InvalidateAsync(
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot<T>> ClearAsync(
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IStateSnapshot<T>> HistoryAsync(
        StateHistoryOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StateChange<T>> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IStatesman : IAsyncDisposable
{
    string Id { get; }

    StatesmanManifest Manifest { get; }

    bool IsInitialized { get; }

    IState<T> State<T>(StateKey<T> key, StatePartition? partition = null);

    /// <summary>Returns a scoped view over a declared attached or isolated container.</summary>
    IStateContainer Container(StatePath path);

    ValueTask<IStateSnapshot> GetAsync(
        StateReference reference,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> SetAsync(
        StateReference reference,
        object value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> InvalidateAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> ClearAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IStateSnapshot> HistoryAsync(
        StateReference reference,
        StateHistoryOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StateChange> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<StateSnapshotSet> CaptureAsync(
        IEnumerable<StateReference> references,
        StateCaptureConsistency required = StateCaptureConsistency.ProcessLocal,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask SignalAsync(StateSignal signal, CancellationToken cancellationToken = default);

    ValueTask MaintainAsync(CancellationToken cancellationToken = default);
}


public interface IStateContainer
{
    string RootId { get; }

    StatePath Path { get; }

    StateContainerIsolation Isolation { get; }

    StateContainerManifest Manifest { get; }

    IState<T> State<T>(StateKey<T> key, StatePartition? partition = null);

    ValueTask<IStateSnapshot> GetAsync(
        StateReference reference,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> SetAsync(
        StateReference reference,
        object value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> InvalidateAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<IStateSnapshot> ClearAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IStateSnapshot> HistoryAsync(
        StateReference reference,
        StateHistoryOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StateChange> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<StateSnapshotSet> CaptureAsync(
        IEnumerable<StateReference> references,
        StateCaptureConsistency required = StateCaptureConsistency.ProcessLocal,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask SignalAsync(StateSignal signal, CancellationToken cancellationToken = default);
}

public interface IStatesmanRegistry
{
    IReadOnlyCollection<IStatesman> All { get; }

    IStatesman Get(string id);

    bool TryGet(string id, out IStatesman? statesman);
}

public sealed class StateDeclarationException : InvalidOperationException
{
    public StateDeclarationException(string message)
        : base(message)
    {
    }
}

public sealed class StateTypeMismatchException : InvalidOperationException
{
    public StateTypeMismatchException(StatePath path, Type expected, Type actual)
        : base($"State '{path}' is declared as '{expected.FullName}', not '{actual.FullName}'.")
    {
    }
}

public sealed class StateUnavailableException : InvalidOperationException
{
    public StateUnavailableException(StateAddress address, StateStatus status, StateError? error)
        : base($"State '{address}' is unavailable with status '{status}'. {error?.Message}".Trim())
    {
        Address = address;
        Status = status;
        Error = error;
    }

    public StateAddress Address { get; }

    public StateStatus Status { get; }

    public StateError? Error { get; }
}

public sealed class StateConcurrencyException : InvalidOperationException
{
    public StateConcurrencyException(StateAddress address, long? expected, long? actual)
        : base($"State '{address}' changed concurrently. Expected revision '{expected?.ToString() ?? "<absent>"}', actual '{actual?.ToString() ?? "<absent>"}'.")
    {
        Address = address;
        Expected = expected;
        Actual = actual;
    }

    public StateAddress Address { get; }

    public long? Expected { get; }

    public long? Actual { get; }
}

public sealed class StateInteractionRejectedException : InvalidOperationException
{
    public StateInteractionRejectedException(StateAddress address, string interaction, IReadOnlyList<string> reasons)
        : base($"Interaction '{interaction}' was rejected for '{address}': {string.Join("; ", reasons)}")
    {
        Address = address;
        Interaction = interaction;
        Reasons = reasons;
    }

    public StateAddress Address { get; }

    public string Interaction { get; }

    public IReadOnlyList<string> Reasons { get; }
}


public sealed class StateInvariantException : InvalidOperationException
{
    public StateInvariantException(StatePath path, string message)
        : base($"State invariant failed for '{path}': {message}")
    {
        Path = path;
    }

    public StatePath Path { get; }
}
