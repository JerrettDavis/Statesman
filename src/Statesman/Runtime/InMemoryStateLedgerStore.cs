using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Statesman;

public sealed class InMemoryStateLedgerStore : IStateLedgerStore, IStateLedgerReplica, IStateChangeFeed, IPartitionCatalog, IStateChangeNotifier
{
    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<StateRecord> _changes = new();

    // Commit-time position allocation. Allocating a position, building the record, appending it to
    // its stream, and publishing it to the change feed happen as one step under this lock, so a
    // position can never become readable on the feed while a lower one is still unpublished. That
    // is what lets a consumer resume from a cursor without skipping a record. Because every
    // enqueue happens here, _changes is in strict position order and ConcurrentQueue<T>
    // enumeration is a moment-in-time snapshot, so a reader always sees a prefix of the published
    // sequence -- which is why ReadAsync needs no lock of its own.
    private readonly object _feedLock = new();

    // One bounded capacity-1 channel per subscriber, in DropWrite mode: a hint raised while a
    // subscriber's channel is full is discarded and TryWrite still reports success, so a slow or
    // idle subscriber can never apply backpressure to a writer and a burst of appends collapses to
    // at most one pending hint each. This is the mechanism behind IStateChangeNotifier's "hints may
    // be coalesced or dropped" clause; StateChangeHub uses the sibling DropOldest mode for the same
    // reason.
    private readonly ConcurrentDictionary<Guid, Channel<StateChangeNotification>> _subscribers = new();

    // Guards SubscribeAsync against racing DisposeAsync. DisposeAsync sets this to 1 before it
    // starts completing subscriber channels; SubscribeAsync re-checks it only after registering
    // its own channel. That ordering closes every race window: if a registration happens before
    // this flag is set, DisposeAsync's completion loop will find and complete that channel; if the
    // flag is already set by the time SubscribeAsync checks, its own re-check completes the
    // channel instead (TryComplete is idempotent, so both sides racing to complete the same
    // channel is harmless). Without this, a subscription that starts during or after disposal
    // would register a channel nothing ever writes to or completes, hanging its caller's
    // MoveNextAsync forever -- contradicting IStateChangeNotifier.SubscribeAsync's documented
    // promise that the sequence "ends when the subscription is cancelled or the store is
    // disposed."
    private int _disposed;

    private readonly TimeProvider _timeProvider;
    private long _globalPosition;

    public InMemoryStateLedgerStore(string name = "memory", TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A store name is required.", nameof(name));
        }

        Name = name;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    public async ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        if (!_streams.TryGetValue(address.Canonical, out StreamState? stream))
        {
            return null;
        }

        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return stream.Records.Count == 0 ? null : Clone(stream.Records[^1]);
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        address.Validate();
        options.Validate();
        if (!_streams.TryGetValue(address.Canonical, out StreamState? stream))
        {
            yield break;
        }

        StateRecord[] records;
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IEnumerable<StateRecord> query = stream.Records;
            if (options.BeforeRevision is long before)
            {
                query = query.Where(record => record.Revision < before);
            }

            if (options.Since is DateTimeOffset since)
            {
                query = query.Where(record => record.OccurredAt >= since);
            }

            query = options.NewestFirst
                ? query.OrderByDescending(record => record.Revision)
                : query.OrderBy(record => record.Revision);
            if (options.Take is int take)
            {
                query = query.Take(take);
            }

            records = query.Select(Clone).ToArray();
        }
        finally
        {
            stream.Gate.Release();
        }

        foreach (StateRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    public async ValueTask<StateAppendResult> AppendAsync(
        StateAddress address,
        StateWriteCondition condition,
        StateCommit commit,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        condition.Validate();
        commit.Validate();
        StreamState stream = _streams.GetOrAdd(address.Canonical, _ => new StreamState());
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateRecord? current = stream.Records.Count == 0 ? null : stream.Records[^1];
            if (!Matches(current, condition))
            {
                return StateAppendResult.Conflict(current is null ? null : Clone(current));
            }

            // The payload and metadata copies are the expensive part of an append and do not
            // touch any shared state, so they run here, before _feedLock, rather than inside it.
            // The record stored on the stream and the clone enqueued to the feed each need their
            // own independent copy -- sharing one mutable byte[] between them would let a caller
            // who mutates one corrupt the other -- so both copies are made up front.
            byte[]? payload = commit.Payload?.ToArray();
            IReadOnlyDictionary<string, string> metadata = Copy(commit.Metadata);
            byte[]? enqueuedPayload = commit.Payload?.ToArray();
            IReadOnlyDictionary<string, string> enqueuedMetadata = Copy(commit.Metadata);

            StateRecord record;
            lock (_feedLock)
            {
                record = new StateRecord
                {
                    Address = address,
                    Revision = (current?.Revision ?? 0) + 1,
                    GlobalPosition = ++_globalPosition,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    Operation = commit.Operation,
                    Status = commit.Status,
                    ValueType = commit.ValueType,
                    SchemaVersion = commit.SchemaVersion,
                    Payload = payload,
                    FreshUntil = commit.FreshUntil,
                    ServeUntil = commit.ServeUntil,
                    Source = commit.Source,
                    CorrelationId = commit.CorrelationId,
                    CausationId = commit.CausationId,
                    Metadata = metadata,
                    Error = commit.Error,
                };
                stream.Records.Add(record);
                _changes.Enqueue(record with { Payload = enqueuedPayload, Metadata = enqueuedMetadata });
            }

            // Raised after _feedLock is released, never inside it. That lock is held by a thread
            // that also holds this stream's gate, so running subscriber code under it would invert
            // lock order against every other append -- and Phase 8 deliberately moved work OUT of
            // this lock. One ordering nuance follows and is admitted by the contract rather than
            // fixed: between the lock release and this call, a later record can be enqueued and
            // hinted first. Both hints say "poll now"; the feed's order is unaffected.
            NotifySubscribers();

            return StateAppendResult.Appended(Clone(record));
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public async ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        StreamState stream = _streams.GetOrAdd(record.Address.Canonical, _ => new StreamState());
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = stream.Records.FindIndex(value => value.Revision == record.Revision);
            bool isNewPosition = index < 0 || stream.Records[index].GlobalPosition != record.GlobalPosition;
            if (index >= 0)
            {
                stream.Records[index] = Clone(record);
            }
            else
            {
                stream.Records.Add(Clone(record));
                stream.Records.Sort(static (left, right) => left.Revision.CompareTo(right.Revision));
            }

            // The clone enqueued to the feed is materialised here, before _feedLock, for the same
            // reason AppendAsync hoists its copies: it is the expensive part and touches no shared
            // state. It is built even when isNewPosition later turns out false; that is cheaper
            // than taking the lock twice to find out first.
            StateRecord enqueued = Clone(record);

            // Same critical section as AppendAsync: raising the high-water mark and publishing to
            // the feed must be one step, so no concurrent append can allocate a position at or
            // below an import that has not been published yet.
            lock (_feedLock)
            {
                AdvanceGlobalPositionUnsafe(record.GlobalPosition);
                if (isNewPosition)
                {
                    _changes.Enqueue(enqueued);
                }
            }

            // Only a genuinely new position is worth a hint, matching the feed enqueue above: a
            // repeated import publishes nothing to the feed, so there is nothing to poll for.
            if (isNewPosition)
            {
                NotifySubscribers();
            }
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public async ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        policy.Validate();
        if (!_streams.TryGetValue(address.Canonical, out StreamState? stream))
        {
            return;
        }

        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stream.Records.Count <= 1)
            {
                return;
            }

            StateRecord latest = stream.Records[^1];
            IEnumerable<StateRecord> retained = stream.Records;
            if (policy.MaxAge is TimeSpan age)
            {
                DateTimeOffset cutoff = _timeProvider.GetUtcNow() - age;
                retained = retained.Where(record => record.OccurredAt >= cutoff || record.Revision == latest.Revision);
            }

            if (!policy.KeepTombstones)
            {
                retained = retained.Where(record => record.Operation != StateOperation.Cleared || record.Revision == latest.Revision);
            }

            List<StateRecord> result = retained.OrderBy(record => record.Revision).ToList();
            if (policy.MaxRevisions is int maxRevisions && result.Count > maxRevisions)
            {
                result = result.Skip(result.Count - maxRevisions).ToList();
            }

            if (policy.MaxBytes is long maxBytes)
            {
                long total = 0;
                var sized = new List<StateRecord>();
                foreach (StateRecord record in result.OrderByDescending(record => record.Revision))
                {
                    long size = record.PayloadLength;
                    if (sized.Count > 0 && total + size > maxBytes)
                    {
                        continue;
                    }

                    sized.Add(record);
                    total += size;
                }

                result = sized.OrderBy(record => record.Revision).ToList();
            }

            stream.Records.Clear();
            stream.Records.AddRange(result);
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public async IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
        StateChangeCursor? from,
        StateChangeReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await Task.CompletedTask;
        long since = from?.Position ?? 0;
        IEnumerable<StateRecord> ordering = _changes
            .Where(record => record.GlobalPosition > since)
            .OrderBy(record => record.GlobalPosition);
        if (options.Take is int take)
        {
            // Applied to the ordering, so it also shrinks the array this materializes -- Take bounds
            // the work here, not just the output.
            ordering = ordering.Take(take);
        }

        StateRecord[] ordered = [.. ordering];

        foreach (StateRecord record in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StateChangeEnvelope
            {
                Record = Clone(record),
                Cursor = new StateChangeCursor(record.GlobalPosition),
            };
        }
    }

    public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (StreamState stream in _streams.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            StateRecord? latest;
            try
            {
                latest = stream.Records.Count == 0 ? null : stream.Records[^1];
            }
            finally
            {
                stream.Gate.Release();
            }

            if (latest is not null)
            {
                yield return new StatePartitionDescriptor
                {
                    Address = latest.Address,
                    LastPosition = new StateChangeCursor(latest.GlobalPosition),
                };
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StateChangeNotification> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        Channel<StateChangeNotification> channel = Channel.CreateBounded<StateChangeNotification>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        // Registered before the first await. An async iterator body runs synchronously up to its
        // first suspension, so the subscription exists as soon as the caller's first MoveNextAsync
        // has returned -- which is what lets a caller subscribe and then write without racing.
        _subscribers[id] = channel;

        // Re-checked only after registering, never before: see the _disposed field comment for why
        // that ordering is what closes the race against DisposeAsync.
        if (Volatile.Read(ref _disposed) != 0)
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await foreach (StateChangeNotification notification in
                channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private void NotifySubscribers()
    {
        foreach (Channel<StateChangeNotification> channel in _subscribers.Values)
        {
            // Non-blocking by construction. A full capacity-1 DropWrite channel discards the hint
            // and still returns true; a completed one returns false. Neither is an error, and
            // neither is worth branching on -- the return value cannot report a drop.
            channel.Writer.TryWrite(default);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Set before completing any channel below, not after: a concurrent SubscribeAsync's
        // re-check reads this flag only once its own channel is already registered, so setting it
        // first guarantees that check either sees 0 (and this loop below will still find and
        // complete that channel) or sees 1 (and completes the channel itself) -- never a gap where
        // neither side does.
        Volatile.Write(ref _disposed, 1);

        // Completing every subscriber's writer is what ends a live `await foreach` over
        // SubscribeAsync instead of leaving it parked forever on a disposed store.
        foreach (Channel<StateChangeNotification> channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }

        _subscribers.Clear();

        foreach (StreamState stream in _streams.Values)
        {
            stream.Gate.Dispose();
        }

        _streams.Clear();
        return ValueTask.CompletedTask;
    }

    private static bool Matches(StateRecord? current, StateWriteCondition condition)
    {
        if (condition.MustBeAbsent)
        {
            return current is null;
        }

        return condition.ExpectedRevision is null || current?.Revision == condition.ExpectedRevision;
    }

    private static StateRecord Clone(StateRecord record) => record with
    {
        Payload = record.Payload?.ToArray(),
        Metadata = Copy(record.Metadata),
    };

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source) =>
        new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

    // The caller must hold _feedLock. The CAS loop this replaces is unnecessary now that every
    // reader and writer of _globalPosition runs under that lock.
    private void AdvanceGlobalPositionUnsafe(long value)
    {
        if (_globalPosition < value)
        {
            _globalPosition = value;
        }
    }

    private sealed class StreamState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<StateRecord> Records { get; } = new();
    }
}
