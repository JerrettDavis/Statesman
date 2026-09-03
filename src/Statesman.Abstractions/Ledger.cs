namespace Statesman;

public sealed record StateError(
    string Code,
    string Message,
    string? ExceptionType = null,
    string? Detail = null,
    bool IsTransient = false);

public sealed record StateRecord
{
    public required StateAddress Address { get; init; }

    public required long Revision { get; init; }

    public required long GlobalPosition { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required StateOperation Operation { get; init; }

    public required StateStatus Status { get; init; }

    public required string ValueType { get; init; }

    public required int SchemaVersion { get; init; }

    public byte[]? Payload { get; init; }

    public DateTimeOffset? FreshUntil { get; init; }

    public DateTimeOffset? ServeUntil { get; init; }

    public required string Source { get; init; }

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public StateError? Error { get; init; }

    public long PayloadLength => Payload?.LongLength ?? 0;
    public void Validate()
    {
        Address.Validate();
        if (Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision), "A ledger revision must be greater than zero.");
        }

        if (GlobalPosition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GlobalPosition), "A global ledger position must be greater than zero.");
        }

        if (OccurredAt == default)
        {
            throw new ArgumentException("A ledger record requires an occurrence time.", nameof(OccurredAt));
        }

        if (Status == StateStatus.Absent)
        {
            throw new ArgumentException("Absent is a synthesized snapshot status and cannot be persisted.", nameof(Status));
        }

        if (string.IsNullOrWhiteSpace(ValueType))
        {
            throw new ArgumentException("A ledger record requires a value type.", nameof(ValueType));
        }

        if (SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "A schema version must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new ArgumentException("A ledger record requires a source.", nameof(Source));
        }

        if ((Status is StateStatus.Ready or StateStatus.Stale) && Payload is null)
        {
            throw new ArgumentException("Ready and stale ledger records require a serialized payload.", nameof(Payload));
        }

        if (Operation == StateOperation.Cleared && Payload is not null)
        {
            throw new ArgumentException("A cleared ledger record cannot contain a payload.", nameof(Payload));
        }

        if (FreshUntil is DateTimeOffset freshUntil &&
            ServeUntil is DateTimeOffset serveUntil &&
            serveUntil < freshUntil)
        {
            throw new ArgumentException("ServeUntil cannot precede FreshUntil.", nameof(ServeUntil));
        }
    }
}

public sealed record StateCommit
{
    public required StateOperation Operation { get; init; }

    public required StateStatus Status { get; init; }

    public required string ValueType { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public byte[]? Payload { get; init; }

    public DateTimeOffset? FreshUntil { get; init; }

    public DateTimeOffset? ServeUntil { get; init; }

    public string Source { get; init; } = "application";

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public StateError? Error { get; init; }

    public void Validate()
    {
        if (Status == StateStatus.Absent)
        {
            throw new ArgumentException("Absent is a synthesized snapshot status and cannot be committed.", nameof(Status));
        }

        if (string.IsNullOrWhiteSpace(ValueType))
        {
            throw new ArgumentException("A state commit requires a value type.", nameof(ValueType));
        }

        if (SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "A schema version must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new ArgumentException("A state commit requires a source.", nameof(Source));
        }

        if ((Status is StateStatus.Ready or StateStatus.Stale) && Payload is null)
        {
            throw new ArgumentException("Ready and stale commits require a serialized payload.", nameof(Payload));
        }

        if (Operation == StateOperation.Cleared && Payload is not null)
        {
            throw new ArgumentException("A cleared commit cannot contain a payload.", nameof(Payload));
        }

        if (FreshUntil is DateTimeOffset freshUntil &&
            ServeUntil is DateTimeOffset serveUntil &&
            serveUntil < freshUntil)
        {
            throw new ArgumentException("ServeUntil cannot precede FreshUntil.", nameof(ServeUntil));
        }
    }
}

public readonly record struct StateWriteCondition(long? ExpectedRevision, bool MustBeAbsent)
{
    public static StateWriteCondition Any { get; } = new(null, false);

    public static StateWriteCondition Absent { get; } = new(null, true);

    public static StateWriteCondition AtRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "An expected revision must be greater than zero.");
        }

        return new StateWriteCondition(revision, false);
    }

    public void Validate()
    {
        if (MustBeAbsent && ExpectedRevision is not null)
        {
            throw new ArgumentException("A write condition cannot require absence and a revision at the same time.");
        }

        if (ExpectedRevision is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedRevision), "An expected revision must be greater than zero.");
        }
    }
}

public sealed record StateAppendResult
{
    private StateAppendResult(bool succeeded, StateRecord? record, StateRecord? current)
    {
        Succeeded = succeeded;
        Record = record;
        Current = current;
    }

    public bool Succeeded { get; }

    public StateRecord? Record { get; }

    public StateRecord? Current { get; }

    public static StateAppendResult Appended(StateRecord record) => new(true, record, record);

    public static StateAppendResult Conflict(StateRecord? current) => new(false, null, current);
}

public interface IStateLedgerStore : IAsyncDisposable
{
    string Name { get; }

    ValueTask<StateRecord?> ReadLatestAsync(StateAddress address, CancellationToken cancellationToken = default);

    IAsyncEnumerable<StateRecord> ReadHistoryAsync(
        StateAddress address,
        StateHistoryOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<StateAppendResult> AppendAsync(
        StateAddress address,
        StateWriteCondition condition,
        StateCommit commit,
        CancellationToken cancellationToken = default);

    ValueTask PruneAsync(
        StateAddress address,
        StateRetentionPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for stores that can accept an exact record from an authoritative ledger.
/// Used by hot/cold stores without inventing a second revision sequence.
/// </summary>
public interface IStateLedgerReplica
{
    ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default);
}

public interface IStateStoreResolver
{
    IStateLedgerStore Resolve(string name);
}
