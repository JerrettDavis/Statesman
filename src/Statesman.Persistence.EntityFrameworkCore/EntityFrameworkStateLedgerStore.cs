using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Statesman;

public sealed class EntityFrameworkStateLedgerStore<TContext> : IStateLedgerStore, IStateLedgerReplica, IStateLeaseProvider, IStateChangeFeed, IPartitionCatalog, IDistributedCapture
    where TContext : StatesmanLedgerDbContext
{
    private const string SequenceName = "global-position";
    private readonly IDbContextFactory<TContext> _factory;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public EntityFrameworkStateLedgerStore(
        string name,
        IDbContextFactory<TContext> factory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    public async ValueTask<StateRecord?> ReadLatestAsync(
        StateAddress address,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        StatesmanLedgerHead? head = await context.StatesmanHeads
            .AsNoTracking()
            .SingleOrDefaultAsync(value =>
                value.Root == address.Root &&
                value.Path == address.Path.Value &&
                value.Partition == address.Partition.Value,
                cancellationToken)
            .ConfigureAwait(false);
        return head is null ? null : ToRecord(head);
    }

    public async IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        address.Validate();
        options.Validate();
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<StatesmanLedgerRecord> query = context.StatesmanRecords
            .AsNoTracking()
            .Where(value =>
                value.Root == address.Root &&
                value.Path == address.Path.Value &&
                value.Partition == address.Partition.Value);
        if (options.BeforeRevision is long before)
        {
            query = query.Where(value => value.Revision < before);
        }

        if (options.Since is DateTimeOffset since)
        {
            query = query.Where(value => value.OccurredAt >= since);
        }

        query = options.NewestFirst
            ? query.OrderByDescending(value => value.Revision)
            : query.OrderBy(value => value.Revision);
        if (options.Take is int take)
        {
            query = query.Take(take);
        }

        List<StatesmanLedgerRecord> records = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (StatesmanLedgerRecord record in records)
        {
            yield return ToRecord(record);
        }
    }

    public async IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
        StateChangeCursor? from,
        StateChangeReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        long since = from?.Position ?? 0;
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<StatesmanLedgerRecord> query = context.StatesmanRecords
            .AsNoTracking()
            .Where(value => value.GlobalPosition > since)
            .OrderBy(value => value.GlobalPosition);
        if (options.Take is int take)
        {
            // After OrderBy, which is what decides WHICH rows the LIMIT keeps. Server-side, the same
            // pattern ReadHistoryAsync already uses for StateHistoryOptions.Take above.
            query = query.Take(take);
        }

        await foreach (StatesmanLedgerRecord entity in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            StateRecord record = ToRecord(entity);
            yield return new StateChangeEnvelope
            {
                Record = record,
                Cursor = new StateChangeCursor(record.GlobalPosition),
            };
        }
    }

    public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<StatesmanLedgerHead> query = context.StatesmanHeads.AsNoTracking();

        await foreach (StatesmanLedgerHead head in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new StatePartitionDescriptor
            {
                Address = new StateAddress(head.Root, new StatePath(head.Path), new StatePartition(head.Partition)),
                LastPosition = new StateChangeCursor(head.GlobalPosition),
            };
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
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            StatesmanLedgerHead? head = await context.StatesmanHeads.SingleOrDefaultAsync(value =>
                value.Root == address.Root &&
                value.Path == address.Path.Value &&
                value.Partition == address.Partition.Value,
                cancellationToken).ConfigureAwait(false);
            StateRecord? current = head is null ? null : ToRecord(head);
            if (!Matches(current, condition))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return StateAppendResult.Conflict(current);
            }

            StatesmanLedgerSequence? sequence = await context.StatesmanSequences
                .SingleOrDefaultAsync(value => value.Name == SequenceName, cancellationToken)
                .ConfigureAwait(false);
            if (sequence is null)
            {
                sequence = new StatesmanLedgerSequence { Name = SequenceName, Value = 1 };
                context.StatesmanSequences.Add(sequence);
            }
            else
            {
                sequence.Value++;
            }

            var record = new StateRecord
            {
                Address = address,
                Revision = (current?.Revision ?? 0) + 1,
                GlobalPosition = sequence.Value,
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
            context.StatesmanRecords.Add(ToEntity(record));
            if (head is null)
            {
                context.StatesmanHeads.Add(ToHead(record));
            }
            else
            {
                Apply(head, record);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return StateAppendResult.Appended(record);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            StateRecord? current = await ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            if (!Matches(current, condition))
            {
                return StateAppendResult.Conflict(current);
            }

            // The write condition still matches, so this was not an optimistic
            // concurrency conflict. Preserve the provider failure for the caller.
            throw;
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

        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            StatesmanLedgerLease? existing = await context.StatesmanLeases
                .SingleOrDefaultAsync(value => value.LeaseId == leaseId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null && existing.ExpiresAt > now)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            string token = Guid.NewGuid().ToString("N");
            DateTimeOffset expiresAt = now + ttl;
            if (existing is null)
            {
                context.StatesmanLeases.Add(new StatesmanLedgerLease { LeaseId = leaseId, Token = token, ExpiresAt = expiresAt });
            }
            else
            {
                existing.Token = token;
                existing.ExpiresAt = expiresAt;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EntityFrameworkLease<TContext>(_factory, leaseId, token, _timeProvider);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await using TContext verifyContext = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            StatesmanLedgerLease? current = await verifyContext.StatesmanLeases
                .AsNoTracking()
                .SingleOrDefaultAsync(value => value.LeaseId == leaseId, cancellationToken)
                .ConfigureAwait(false);
            if (current is not null && current.ExpiresAt > _timeProvider.GetUtcNow())
            {
                // Another caller's concurrent first acquisition won the race. This is the
                // documented "someone else got it" outcome, not a failure.
                return null;
            }

            // The lease is not actually held by anyone else, so this was not an optimistic
            // concurrency conflict. Preserve the provider failure for the caller.
            throw;
        }
    }

    public async ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
        IEnumerable<StateAddress> addresses,
        StateCaptureConsistency required,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        StateAddress[] targets = addresses.Distinct().ToArray();
        foreach (StateAddress address in targets)
        {
            address.Validate();
        }

        // Serializable is strictly sufficient everywhere and matches the AppendAsync/AcquireAsync
        // transaction shape, but IsolationLevel.Snapshot is the semantically closer mapping on
        // providers that offer it (SQL Server, PostgreSQL's REPEATABLE READ) — worth revisiting if
        // SQLite's BEGIN IMMEDIATE write stall (see docs/providers/index.md) becomes a real problem.
        IsolationLevel isolationLevel = required switch
        {
            StateCaptureConsistency.ReadCommittedDistributed => IsolationLevel.ReadCommitted,
            StateCaptureConsistency.SnapshotDistributed => IsolationLevel.Serializable,
            _ => throw new ArgumentOutOfRangeException(nameof(required), required,
                "EntityFrameworkStateLedgerStore only backs distributed consistency levels."),
        };

        if (targets.Length == 0)
        {
            return new Dictionary<StateAddress, StateRecord?>();
        }

        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<StateAddress, StateRecord?>();
        foreach (StateAddress address in targets)
        {
            StatesmanLedgerHead? head = await context.StatesmanHeads
                .AsNoTracking()
                .SingleOrDefaultAsync(value =>
                    value.Root == address.Root &&
                    value.Path == address.Path.Value &&
                    value.Partition == address.Partition.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            result[address] = head is null ? null : ToRecord(head);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        StatesmanLedgerRecord? existing = await context.StatesmanRecords.SingleOrDefaultAsync(value =>
            value.Root == record.Address.Root &&
            value.Path == record.Address.Path.Value &&
            value.Partition == record.Address.Partition.Value &&
            value.Revision == record.Revision,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.StatesmanRecords.Add(ToEntity(record));
        }
        else
        {
            // Replica import is exact. A matching key does not prove the cached
            // record is identical to the cold authority.
            Apply(existing, record);
        }

        StatesmanLedgerHead? head = await context.StatesmanHeads.SingleOrDefaultAsync(value =>
            value.Root == record.Address.Root &&
            value.Path == record.Address.Path.Value &&
            value.Partition == record.Address.Partition.Value,
            cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            context.StatesmanHeads.Add(ToHead(record));
        }
        else if (head.Revision <= record.Revision)
        {
            Apply(head, record);
        }

        StatesmanLedgerSequence? sequence = await context.StatesmanSequences
            .SingleOrDefaultAsync(value => value.Name == SequenceName, cancellationToken)
            .ConfigureAwait(false);
        if (sequence is null)
        {
            context.StatesmanSequences.Add(new StatesmanLedgerSequence
            {
                Name = SequenceName,
                Value = record.GlobalPosition,
            });
        }
        else if (sequence.Value < record.GlobalPosition)
        {
            sequence.Value = record.GlobalPosition;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        address.Validate();
        policy.Validate();
        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<StatesmanLedgerRecord> records = await context.StatesmanRecords
            .Where(value =>
                value.Root == address.Root &&
                value.Path == address.Path.Value &&
                value.Partition == address.Partition.Value)
            .OrderBy(value => value.Revision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (records.Count <= 1)
        {
            return;
        }

        long latest = records[^1].Revision;
        IEnumerable<StatesmanLedgerRecord> retained = records;
        if (policy.MaxAge is TimeSpan age)
        {
            DateTimeOffset cutoff = _timeProvider.GetUtcNow() - age;
            retained = retained.Where(value => value.OccurredAt >= cutoff || value.Revision == latest);
        }

        if (!policy.KeepTombstones)
        {
            retained = retained.Where(value => value.Operation != StateOperation.Cleared || value.Revision == latest);
        }

        List<StatesmanLedgerRecord> keep = retained.OrderBy(value => value.Revision).ToList();
        if (policy.MaxRevisions is int maxRevisions && keep.Count > maxRevisions)
        {
            keep = keep.Skip(keep.Count - maxRevisions).ToList();
        }

        if (policy.MaxBytes is long maxBytes)
        {
            long bytes = 0;
            var sized = new List<StatesmanLedgerRecord>();
            foreach (StatesmanLedgerRecord record in keep.OrderByDescending(value => value.Revision))
            {
                long length = record.Payload?.LongLength ?? 0;
                if (sized.Count > 0 && bytes + length > maxBytes)
                {
                    continue;
                }

                sized.Add(record);
                bytes += length;
            }

            keep = sized.OrderBy(value => value.Revision).ToList();
        }

        HashSet<long> keepRevisions = keep.Select(value => value.Revision).ToHashSet();
        context.StatesmanRecords.RemoveRange(records.Where(value => !keepRevisions.Contains(value.Revision)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private StatesmanLedgerRecord ToEntity(StateRecord record) => new()
    {
        Root = record.Address.Root,
        Path = record.Address.Path.Value,
        Partition = record.Address.Partition.Value,
        Revision = record.Revision,
        GlobalPosition = record.GlobalPosition,
        OccurredAt = record.OccurredAt,
        Operation = record.Operation,
        Status = record.Status,
        ValueType = record.ValueType,
        SchemaVersion = record.SchemaVersion,
        Payload = record.Payload?.ToArray(),
        FreshUntil = record.FreshUntil,
        ServeUntil = record.ServeUntil,
        Source = record.Source,
        CorrelationId = record.CorrelationId,
        CausationId = record.CausationId,
        MetadataJson = JsonSerializer.Serialize(record.Metadata, _json),
        ErrorJson = record.Error is null ? null : JsonSerializer.Serialize(record.Error, _json),
    };

    private StatesmanLedgerHead ToHead(StateRecord record)
    {
        var head = new StatesmanLedgerHead();
        Apply(head, record);
        return head;
    }

    private void Apply(StatesmanLedgerRecord entity, StateRecord record)
    {
        entity.Root = record.Address.Root;
        entity.Path = record.Address.Path.Value;
        entity.Partition = record.Address.Partition.Value;
        entity.Revision = record.Revision;
        entity.GlobalPosition = record.GlobalPosition;
        entity.OccurredAt = record.OccurredAt;
        entity.Operation = record.Operation;
        entity.Status = record.Status;
        entity.ValueType = record.ValueType;
        entity.SchemaVersion = record.SchemaVersion;
        entity.Payload = record.Payload?.ToArray();
        entity.FreshUntil = record.FreshUntil;
        entity.ServeUntil = record.ServeUntil;
        entity.Source = record.Source;
        entity.CorrelationId = record.CorrelationId;
        entity.CausationId = record.CausationId;
        entity.MetadataJson = JsonSerializer.Serialize(record.Metadata, _json);
        entity.ErrorJson = record.Error is null ? null : JsonSerializer.Serialize(record.Error, _json);
    }

    private void Apply(StatesmanLedgerHead head, StateRecord record)
    {
        head.Root = record.Address.Root;
        head.Path = record.Address.Path.Value;
        head.Partition = record.Address.Partition.Value;
        head.Revision = record.Revision;
        head.GlobalPosition = record.GlobalPosition;
        head.OccurredAt = record.OccurredAt;
        head.Operation = record.Operation;
        head.Status = record.Status;
        head.ValueType = record.ValueType;
        head.SchemaVersion = record.SchemaVersion;
        head.Payload = record.Payload?.ToArray();
        head.FreshUntil = record.FreshUntil;
        head.ServeUntil = record.ServeUntil;
        head.Source = record.Source;
        head.CorrelationId = record.CorrelationId;
        head.CausationId = record.CausationId;
        head.MetadataJson = JsonSerializer.Serialize(record.Metadata, _json);
        head.ErrorJson = record.Error is null ? null : JsonSerializer.Serialize(record.Error, _json);
    }

    private StateRecord ToRecord(StatesmanLedgerHead head) => new()
    {
        Address = new StateAddress(head.Root, new StatePath(head.Path), new StatePartition(head.Partition)),
        Revision = head.Revision,
        GlobalPosition = head.GlobalPosition,
        OccurredAt = head.OccurredAt,
        Operation = head.Operation,
        Status = head.Status,
        ValueType = head.ValueType,
        SchemaVersion = head.SchemaVersion,
        Payload = head.Payload?.ToArray(),
        FreshUntil = head.FreshUntil,
        ServeUntil = head.ServeUntil,
        Source = head.Source,
        CorrelationId = head.CorrelationId,
        CausationId = head.CausationId,
        Metadata = DeserializeMetadata(head.MetadataJson),
        Error = DeserializeError(head.ErrorJson),
    };

    private StateRecord ToRecord(StatesmanLedgerRecord entity) => new()
    {
        Address = new StateAddress(entity.Root, new StatePath(entity.Path), new StatePartition(entity.Partition)),
        Revision = entity.Revision,
        GlobalPosition = entity.GlobalPosition,
        OccurredAt = entity.OccurredAt,
        Operation = entity.Operation,
        Status = entity.Status,
        ValueType = entity.ValueType,
        SchemaVersion = entity.SchemaVersion,
        Payload = entity.Payload?.ToArray(),
        FreshUntil = entity.FreshUntil,
        ServeUntil = entity.ServeUntil,
        Source = entity.Source,
        CorrelationId = entity.CorrelationId,
        CausationId = entity.CausationId,
        Metadata = DeserializeMetadata(entity.MetadataJson),
        Error = DeserializeError(entity.ErrorJson),
    };

    private IReadOnlyDictionary<string, string> DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, _json)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private StateError? DeserializeError(string? json) => string.IsNullOrWhiteSpace(json)
        ? null
        : JsonSerializer.Deserialize<StateError>(json, _json);

    private static bool Matches(StateRecord? current, StateWriteCondition condition)
    {
        if (condition.MustBeAbsent)
        {
            return current is null;
        }

        return condition.ExpectedRevision is null || current?.Revision == condition.ExpectedRevision;
    }
}

internal sealed class EntityFrameworkLease<TContext> : IStateLease
    where TContext : StatesmanLedgerDbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly string _leaseId;
    private readonly string _token;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public EntityFrameworkLease(
        IDbContextFactory<TContext> factory, string leaseId, string token, TimeProvider timeProvider)
    {
        _factory = factory;
        _leaseId = leaseId;
        _token = token;
        _timeProvider = timeProvider;
    }

    public async ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "A lease TTL must be greater than zero.");
        }

        await using TContext context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        StatesmanLedgerLease? existing = await context.StatesmanLeases
            .SingleOrDefaultAsync(value => value.LeaseId == _leaseId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // The row being gone, or carrying another token, means someone else holds it. ExpiresAt
        // having passed means this lease is lost too, even with nobody else racing: AcquireAsync
        // grants an expired row to a new caller without consulting this holder, so extending it
        // here would produce two live handles for one lease id. Judged against the same
        // TimeProvider AcquireAsync judges expiry by, so the two halves of this provider agree
        // about when a lease ended.
        if (existing is null || existing.Token != _token || existing.ExpiresAt <= now)
        {
            return false;
        }

        existing.ExpiresAt = now + ttl;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // StatesmanLedgerLease.ExpiresAt is a concurrency token, so a re-acquisition that
            // rewrote this row between the read above and this save makes the UPDATE match zero
            // rows. The lease was lost by the contract's own definition, and the contract says that
            // is false rather than an exception -- the same reason AcquireAsync returns null on
            // DbUpdateException and DisposeAsync swallows it on release. Deliberately the DERIVED
            // type: a plain DbUpdateException is a genuine provider failure and must still surface.
            return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await using TContext context = await _factory.CreateDbContextAsync().ConfigureAwait(false);
        StatesmanLedgerLease? existing = await context.StatesmanLeases
            .SingleOrDefaultAsync(value => value.LeaseId == _leaseId)
            .ConfigureAwait(false);
        if (existing is not null && existing.Token == _token)
        {
            context.StatesmanLeases.Remove(existing);
            try
            {
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
            }
        }
    }
}
