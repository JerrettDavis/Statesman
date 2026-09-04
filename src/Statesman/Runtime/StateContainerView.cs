using System.Runtime.CompilerServices;

namespace Statesman;

internal sealed class StateContainerView : IStateContainer
{
    private readonly StatesmanRuntime _runtime;

    public StateContainerView(StatesmanRuntime runtime, StateContainerManifest manifest)
    {
        _runtime = runtime;
        Manifest = manifest;
    }

    public string RootId => _runtime.Id;

    public StatePath Path => Manifest.Path;

    public StateContainerIsolation Isolation => Manifest.Isolation;

    public StateContainerManifest Manifest { get; }

    public IState<T> State<T>(StateKey<T> key, StatePartition? partition = null)
    {
        EnsureInScope(key.Path);
        return _runtime.State(key, partition);
    }

    public ValueTask<IStateSnapshot> GetAsync(
        StateReference reference,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInScope(reference.Path);
        return _runtime.GetAsync(reference, options, cancellationToken);
    }

    public ValueTask<IStateSnapshot> SetAsync(
        StateReference reference,
        object value,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInScope(reference.Path);
        return _runtime.SetAsync(reference, value, options, cancellationToken);
    }

    public ValueTask<IStateSnapshot> InvalidateAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInScope(reference.Path);
        return _runtime.InvalidateAsync(reference, reason, options, cancellationToken);
    }

    public ValueTask<IStateSnapshot> ClearAsync(
        StateReference reference,
        string? reason = null,
        StateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInScope(reference.Path);
        return _runtime.ClearAsync(reference, reason, options, cancellationToken);
    }

    public async IAsyncEnumerable<IStateSnapshot> HistoryAsync(
        StateReference reference,
        StateHistoryOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInScope(reference.Path);
        await foreach (IStateSnapshot snapshot in _runtime.HistoryAsync(reference, options, cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    public IAsyncEnumerable<StateChange> ObserveAsync(
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _runtime.ObserveAsync(Path, options, cancellationToken);

    public ValueTask<StateSnapshotSet> CaptureAsync(
        IEnumerable<StateReference> references,
        StateCaptureConsistency required = StateCaptureConsistency.ProcessLocal,
        StateReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        StateReference[] requested = references.ToArray();
        foreach (StateReference reference in requested)
        {
            EnsureInScope(reference.Path);
        }

        return _runtime.CaptureAsync(requested, required, options, cancellationToken);
    }

    public ValueTask SignalAsync(StateSignal signal, CancellationToken cancellationToken = default) =>
        _runtime.SignalAsync(signal, Path, cancellationToken);

    private bool IsInScope(StatePath path) => Path.IsRoot || path.IsDescendantOf(Path);

    private void EnsureInScope(StatePath path)
    {
        if (!IsInScope(path))
        {
            throw new StateDeclarationException(
                $"State '{path}' is outside container '{Path}' in Statesman root '{RootId}'.");
        }
    }
}
