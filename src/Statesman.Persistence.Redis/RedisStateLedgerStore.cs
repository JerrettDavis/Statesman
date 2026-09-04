using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Statesman;

public sealed class RedisStateLedgerStoreOptions
{
    public string KeyPrefix { get; set; } = "statesman";

    public int Database { get; set; } = -1;

    public bool OwnsConnection { get; set; }
}

public sealed class RedisStateLedgerStore : IStateLedgerStore, IStateLeaseProvider, IStateChangeFeed
{
    private const int MaxAppendAttempts = 16;
    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly RedisStateLedgerStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public RedisStateLedgerStore(
        string name,
        IConnectionMultiplexer connection,
        RedisStateLedgerStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new RedisStateLedgerStoreOptions();
        _database = connection.GetDatabase(_options.Database);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    public async ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue value = await _database.StringGetAsync(HeadKey(address)).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : Deserialize(value!);
    }

    public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        address.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue[] values = await _database.SortedSetRangeByRankAsync(
            HistoryKey(address),
            start: 0,
            stop: -1,
            order: options.NewestFirst ? Order.Descending : Order.Ascending).ConfigureAwait(false);

        IEnumerable<StateRecord> records = values
            .Where(value => !value.IsNullOrEmpty)
            .Select(value => Deserialize(value!));
        if (options.BeforeRevision is long before)
        {
            records = records.Where(record => record.Revision < before);
        }

        if (options.Since is DateTimeOffset since)
        {
            records = records.Where(record => record.OccurredAt >= since);
        }

        if (options.Take is int take)
        {
            records = records.Take(take);
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
        bool mayRetry = !condition.MustBeAbsent && condition.ExpectedRevision is null;

        for (int attempt = 1; attempt <= MaxAppendAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateRecord? current = await ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            if (!Matches(current, condition))
            {
                return StateAppendResult.Conflict(current);
            }

            long revision = (current?.Revision ?? 0) + 1;
            long position = await _database.StringIncrementAsync(GlobalPositionKey()).ConfigureAwait(false);
            var record = new StateRecord
            {
                Address = address,
                Revision = revision,
                GlobalPosition = position,
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
                Metadata = new Dictionary<string, string>(commit.Metadata, StringComparer.OrdinalIgnoreCase),
                Error = commit.Error,
            };
            RedisValue serialized = Serialize(record);
            RedisKey revisionKey = RevisionKey(address);
            ITransaction transaction = _database.CreateTransaction();

            // Always compare against the revision that was actually observed. This
            // gives even unconditional writes a linearizable stream revision; they
            // retry rather than silently producing duplicate revisions.
            if (current is null)
            {
                transaction.AddCondition(Condition.KeyNotExists(revisionKey));
            }
            else
            {
                transaction.AddCondition(Condition.StringEqual(
                    revisionKey,
                    current.Revision.ToString(CultureInfo.InvariantCulture)));
            }

            _ = transaction.StringSetAsync(HeadKey(address), serialized);
            _ = transaction.StringSetAsync(revisionKey, revision.ToString(CultureInfo.InvariantCulture));
            _ = transaction.SortedSetAddAsync(HistoryKey(address), serialized, revision);
            _ = transaction.SortedSetAddAsync(ChangeFeedKey(), serialized, position);
            bool committed = await transaction.ExecuteAsync().ConfigureAwait(false);
            if (committed)
            {
                return StateAppendResult.Appended(record);
            }

            StateRecord? latest = await ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            if (!mayRetry)
            {
                return StateAppendResult.Conflict(latest);
            }
        }

        throw new InvalidOperationException(
            $"Redis could not append '{address.Canonical}' after {MaxAppendAttempts} optimistic retries.");
    }

    public async ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        RedisKey historyKey = HistoryKey(address);
        RedisValue[] values = await _database.SortedSetRangeByRankAsync(
            historyKey,
            start: 0,
            stop: -1,
            order: Order.Ascending).ConfigureAwait(false);
        if (values.Length <= 1)
        {
            return;
        }

        var records = values.Select(value => (Value: value, Record: Deserialize(value!))).ToList();
        long latest = records.Max(value => value.Record.Revision);
        IEnumerable<(RedisValue Value, StateRecord Record)> retained = records;
        if (policy.MaxAge is TimeSpan age)
        {
            DateTimeOffset cutoff = _timeProvider.GetUtcNow() - age;
            retained = retained.Where(value => value.Record.OccurredAt >= cutoff || value.Record.Revision == latest);
        }

        if (!policy.KeepTombstones)
        {
            retained = retained.Where(value => value.Record.Operation != StateOperation.Cleared || value.Record.Revision == latest);
        }

        List<(RedisValue Value, StateRecord Record)> keep = retained.OrderBy(value => value.Record.Revision).ToList();
        if (policy.MaxRevisions is int maxRevisions && keep.Count > maxRevisions)
        {
            keep = keep.Skip(keep.Count - maxRevisions).ToList();
        }

        if (policy.MaxBytes is long maxBytes)
        {
            long bytes = 0;
            var sized = new List<(RedisValue Value, StateRecord Record)>();
            foreach ((RedisValue value, StateRecord record) in keep.OrderByDescending(value => value.Record.Revision))
            {
                long length = record.PayloadLength;
                if (sized.Count > 0 && bytes + length > maxBytes)
                {
                    continue;
                }

                sized.Add((value, record));
                bytes += length;
            }

            keep = sized.OrderBy(value => value.Record.Revision).ToList();
        }

        HashSet<long> keepRevisions = keep.Select(value => value.Record.Revision).ToHashSet();
        RedisValue[] remove = records
            .Where(value => !keepRevisions.Contains(value.Record.Revision))
            .Select(value => value.Value)
            .ToArray();
        if (remove.Length > 0)
        {
            await _database.SortedSetRemoveAsync(historyKey, remove).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_options.OwnsConnection)
        {
            _connection.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private RedisKey HeadKey(StateAddress address) => $"{StreamKey(address)}:head";

    private RedisKey RevisionKey(StateAddress address) => $"{StreamKey(address)}:revision";

    private RedisKey HistoryKey(StateAddress address) => $"{StreamKey(address)}:history";

    private RedisKey GlobalPositionKey() => $"{_options.KeyPrefix}:{Name}:global-position";

    private RedisKey ChangeFeedKey() => $"{_options.KeyPrefix}:{Name}:changes";

    public async IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
        StateChangeCursor? from,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        double start = from?.Position ?? 0;
        RedisValue[] values = await _database
            .SortedSetRangeByScoreAsync(ChangeFeedKey(), start: start, exclude: Exclude.Start)
            .ConfigureAwait(false);

        foreach (RedisValue value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateRecord record = Deserialize(value!);
            yield return new StateChangeEnvelope
            {
                Record = record,
                Cursor = new StateChangeCursor(record.GlobalPosition),
            };
        }
    }

    public async ValueTask<IStateLease?> AcquireAsync(
        string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "A lease TTL must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string token = Guid.NewGuid().ToString("N");
        RedisKey key = LeaseKey(leaseId);
        bool acquired = await _database.StringSetAsync(key, token, ttl, When.NotExists).ConfigureAwait(false);
        return acquired ? new RedisLease(_database, key, token) : null;
    }

    private RedisKey LeaseKey(string leaseId) => $"{_options.KeyPrefix}:{Name}:lease:{leaseId}";

    private string StreamKey(StateAddress address)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Canonical))).ToLowerInvariant();
        return $"{_options.KeyPrefix}:{Name}:stream:{hash}";
    }

    private RedisValue Serialize(StateRecord record) => JsonSerializer.Serialize(new RedisRecord(record), _json);

    private StateRecord Deserialize(RedisValue value)
    {
        RedisRecord model = JsonSerializer.Deserialize<RedisRecord>((string)value!, _json)
            ?? throw new InvalidDataException("Redis contained an empty Statesman record.");
        return model.ToStateRecord();
    }

    private static bool Matches(StateRecord? current, StateWriteCondition condition)
    {
        if (condition.MustBeAbsent)
        {
            return current is null;
        }

        return condition.ExpectedRevision is null || current?.Revision == condition.ExpectedRevision;
    }

    private sealed record RedisRecord
    {
        public RedisRecord()
        {
        }

        public RedisRecord(StateRecord record)
        {
            Root = record.Address.Root;
            Path = record.Address.Path.Value;
            Partition = record.Address.Partition.Value;
            Revision = record.Revision;
            GlobalPosition = record.GlobalPosition;
            OccurredAt = record.OccurredAt;
            Operation = record.Operation;
            Status = record.Status;
            ValueType = record.ValueType;
            SchemaVersion = record.SchemaVersion;
            Payload = record.Payload?.ToArray();
            FreshUntil = record.FreshUntil;
            ServeUntil = record.ServeUntil;
            Source = record.Source;
            CorrelationId = record.CorrelationId;
            CausationId = record.CausationId;
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
            Error = record.Error;
        }

        public string Root { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Partition { get; init; } = StatePartition.Default.Value;
        public long Revision { get; init; }
        public long GlobalPosition { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public StateOperation Operation { get; init; }
        public StateStatus Status { get; init; }
        public string ValueType { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public byte[]? Payload { get; init; }
        public DateTimeOffset? FreshUntil { get; init; }
        public DateTimeOffset? ServeUntil { get; init; }
        public string Source { get; init; } = string.Empty;
        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
        public StateError? Error { get; init; }

        public StateRecord ToStateRecord() => new()
        {
            Address = new StateAddress(Root, new StatePath(Path), new StatePartition(Partition)),
            Revision = Revision,
            GlobalPosition = GlobalPosition,
            OccurredAt = OccurredAt,
            Operation = Operation,
            Status = Status,
            ValueType = ValueType,
            SchemaVersion = SchemaVersion,
            Payload = Payload?.ToArray(),
            FreshUntil = FreshUntil,
            ServeUntil = ServeUntil,
            Source = Source,
            CorrelationId = CorrelationId,
            CausationId = CausationId,
            Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase),
            Error = Error,
        };
    }

    private sealed class RedisLease : IStateLease
    {
        private const string RenewScript = """
            if redis.call("get", KEYS[1]) == ARGV[1] then
                return redis.call("pexpire", KEYS[1], ARGV[2])
            end
            return 0
            """;

        private const string ReleaseScript = """
            if redis.call("get", KEYS[1]) == ARGV[1] then
                return redis.call("del", KEYS[1])
            end
            return 0
            """;

        private readonly IDatabase _database;
        private readonly RedisKey _key;
        private readonly string _token;
        private int _disposed;

        public RedisLease(IDatabase database, RedisKey key, string token)
        {
            _database = database;
            _key = key;
            _token = token;
        }

        public async ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "A lease TTL must be greater than zero.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            RedisResult result = await _database
                .ScriptEvaluateAsync(RenewScript, [_key], [_token, (long)ttl.TotalMilliseconds])
                .ConfigureAwait(false);
            return (long)result == 1;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _database.ScriptEvaluateAsync(ReleaseScript, [_key], [_token]).ConfigureAwait(false);
        }
    }
}
