using System.Runtime.CompilerServices;

namespace Statesman;

public sealed class StateSubscription : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal StateSubscription(CancellationTokenSource cancellation, Task completion)
    {
        _cancellation = cancellation;
        Completion = completion;
    }

    public Task Completion { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}

public static class StateObservationExtensions
{
    public static async IAsyncEnumerable<StateChange<T>> ObserveAsync<T>(
        this IState<T> state,
        StateOperation operation,
        StateObservationOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await foreach (StateChange<T> change in state.ObserveAsync(options, cancellationToken).ConfigureAwait(false))
        {
            if (change.Operation == operation)
            {
                yield return change;
            }
        }
    }

    public static StateSubscription OnChange<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, operation: null, handler, options, cancellationToken);

    public static StateSubscription On<T>(
        this IState<T> state,
        StateOperation operation,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, operation, handler, options, cancellationToken);


    public static StateSubscription OnSet<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, StateOperation.Set, handler, options, cancellationToken);

    public static StateSubscription OnTransition<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, StateOperation.Transitioned, handler, options, cancellationToken);

    public static StateSubscription OnInvalidated<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, StateOperation.Invalidated, handler, options, cancellationToken);

    public static StateSubscription OnCleared<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, StateOperation.Cleared, handler, options, cancellationToken);

    public static StateSubscription OnFaulted<T>(
        this IState<T> state,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Subscribe(state, StateOperation.Faulted, handler, options, cancellationToken);

    private static StateSubscription Subscribe<T>(
        IState<T> state,
        StateOperation? operation,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(handler);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StateObservationOptions observation = options ?? new StateObservationOptions { IncludeCurrent = false };
        Task completion = PumpAsync(state, operation, handler, observation, linked.Token);
        return new StateSubscription(linked, completion);
    }

    private static async Task PumpAsync<T>(
        IState<T> state,
        StateOperation? operation,
        Func<StateChange<T>, CancellationToken, ValueTask> handler,
        StateObservationOptions options,
        CancellationToken cancellationToken)
    {
        await foreach (StateChange<T> change in state.ObserveAsync(options, cancellationToken).ConfigureAwait(false))
        {
            if (operation is null || change.Operation == operation)
            {
                await handler(change, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
