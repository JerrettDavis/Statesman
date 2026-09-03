using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Statesman;

internal sealed class StateChangeHub<T>
{
    private readonly ConcurrentDictionary<Guid, Channel<StateChange<T>>> _subscribers = new();

    public async IAsyncEnumerable<StateChange<T>> SubscribeAsync(
        Func<IStateSnapshot<T>> current,
        StateObservationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        var channel = Channel.CreateBounded<StateChange<T>>(new BoundedChannelOptions(options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        Guid id = Guid.NewGuid();
        _subscribers[id] = channel;
        try
        {
            if (options.IncludeCurrent)
            {
                IStateSnapshot<T> snapshot = current();
                if (snapshot.Operation is StateOperation operation)
                {
                    yield return new StateChange<T>(snapshot, snapshot, operation);
                }
            }

            await foreach (StateChange<T> change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            if (_subscribers.TryRemove(id, out Channel<StateChange<T>>? removed))
            {
                removed.Writer.TryComplete();
            }
        }
    }

    public void Publish(StateChange<T> change)
    {
        foreach (Channel<StateChange<T>> subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(change);
        }
    }

    public void Complete()
    {
        foreach (Channel<StateChange<T>> subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }

        _subscribers.Clear();
    }
}

internal sealed class GlobalStateChangeHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public IAsyncEnumerable<StateChange> SubscribeAsync(
        Func<IReadOnlyList<IStateSnapshot>> current,
        StateObservationOptions options,
        CancellationToken cancellationToken) =>
        SubscribeAsync(current, static _ => true, options, cancellationToken);

    public async IAsyncEnumerable<StateChange> SubscribeAsync(
        Func<IReadOnlyList<IStateSnapshot>> current,
        Func<StateChange, bool> filter,
        StateObservationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(filter);
        options.Validate();
        var channel = Channel.CreateBounded<StateChange>(new BoundedChannelOptions(options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        Guid id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(channel, filter);
        try
        {
            if (options.IncludeCurrent)
            {
                foreach (IStateSnapshot snapshot in current())
                {
                    if (snapshot.Operation is StateOperation operation)
                    {
                        yield return new StateChange(snapshot, snapshot, operation);
                    }
                }
            }

            await foreach (StateChange change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            if (_subscribers.TryRemove(id, out Subscriber? removed))
            {
                removed.Channel.Writer.TryComplete();
            }
        }
    }

    public void Publish(StateChange change)
    {
        foreach (Subscriber subscriber in _subscribers.Values)
        {
            if (subscriber.Filter(change))
            {
                subscriber.Channel.Writer.TryWrite(change);
            }
        }
    }

    public void Complete()
    {
        foreach (Subscriber subscriber in _subscribers.Values)
        {
            subscriber.Channel.Writer.TryComplete();
        }

        _subscribers.Clear();
    }

    private sealed record Subscriber(Channel<StateChange> Channel, Func<StateChange, bool> Filter);
}
