using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Statesman;

public sealed class InMemoryStateLedgerStore : IStateLedgerStore, IStateLedgerReplica, IStateChangeFeed
{
    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<StateRecord> _changes = new();
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

            var record = new StateRecord
            {
                Address = address,
                Revision = (current?.Revision ?? 0) + 1,
                GlobalPosition = Interlocked.Increment(ref _globalPosition),
                OccurredAt = _timeProvider.GetUtcNow(),
                Operation = commit.Operation,
                Status = commit.Status,
                ValueType = commit.ValueType,
                SchemaVersion = commit.SchemaVersion,
                Payload = commit.Payload?.ToArray(),
                FreshUntil = commit.FreshUntil,
                ServeUntil = commit.ServeUntil,
                Source = commit.Source,
                CorrelationId = commit.CorrelationId,
                CausationId = commit.CausationId,
                Metadata = Copy(commit.Metadata),
                Error = commit.Error,
            };
            stream.Records.Add(record);
            _changes.Enqueue(Clone(record));
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

            if (isNewPosition)
            {
                _changes.Enqueue(Clone(record));
            }

            AdvanceGlobalPosition(record.GlobalPosition);
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        long since = from?.Position ?? 0;
        StateRecord[] ordered = [.. _changes
            .Where(record => record.GlobalPosition > since)
            .OrderBy(record => record.GlobalPosition)];

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

    public ValueTask DisposeAsync()
    {
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

    private void AdvanceGlobalPosition(long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _globalPosition);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _globalPosition, value, current) != current);
    }

    private sealed class StreamState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<StateRecord> Records { get; } = new();
    }
}
