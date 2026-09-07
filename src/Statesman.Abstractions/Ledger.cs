using System.Diagnostics.CodeAnalysis;

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

/// <summary>
/// Marks an interface as an optional, negotiable capability a ledger store may implement.
/// Callers discover support with <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/>
/// rather than assuming every store provides every guarantee.
/// </summary>
public interface IStateCapability
{
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
/// <remarks>
/// An import carries the record's original <see cref="StateRecord.GlobalPosition"/>, and every
/// provider raises its own position counter to at least that value so later appends cannot reuse it.
/// Importing at a position <i>below</i> the store's current high-water mark — a restore replayed
/// into a store that already holds newer records — therefore lands behind any
/// <see cref="IStateChangeFeed"/> consumer that has already advanced past it, and that consumer is
/// not re-delivered the imported record. That is inherent to restoring into a live store, not a gap
/// in the feed's guarantee.
/// </remarks>
public interface IStateLedgerReplica : IStateCapability
{
    ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for a ledger store that can coordinate exclusive, time-bounded access
/// across processes. Single-process stores (in-memory, filesystem) do not implement this.
/// </summary>
public interface IStateLeaseProvider : IStateCapability
{
    /// <summary>
    /// Attempts to acquire a named lease. Returns <see langword="null"/> if another holder
    /// currently owns it — this is an expected outcome, not a failure.
    /// </summary>
    ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>
/// A held lease. Disposing releases it; renewing extends its time-to-live while still held.
/// </summary>
public interface IStateLease : IAsyncDisposable
{
    /// <summary>Extends the lease's time-to-live. Returns <see langword="false"/> if it was lost.</summary>
    ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for a ledger store that can enumerate its own history across every stream,
/// resumable from an opaque cursor. Unlike <see cref="IStateLedgerStore.ReadHistoryAsync"/> (which
/// is scoped to one already-known address), this reads everything the store has recorded since a
/// point in its global order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee.</b> Records are yielded in ascending position order, and no record is skipped:
/// a consumer that resumes from the cursor of the last record it accepted eventually sees every
/// record the store still retains at a higher position. A record may be yielded more than once
/// across calls, so consumers must be idempotent — this feed is at-least-once, never at-most-once.
/// </para>
/// <para>
/// A provider reaches that by allocating a record's position in the same step that makes the record
/// visible here, so a position never becomes readable before a lower one exists. A write in flight
/// therefore holds back the records committed after it, and the feed's tail can briefly lag the
/// store's newest record. It lags; it does not skip.
/// </para>
/// <para>
/// <b>What the guarantee does not cover.</b> Retention: pruning a stream can remove a record before
/// a consumer reaches it, and the feed does not resurrect it — see each provider's documentation for
/// which of its structures retention actually trims. A record imported through
/// <see cref="IStateLedgerReplica.ImportAsync"/> at a position a consumer has already passed is not
/// re-delivered to that consumer. And on the filesystem provider a crash between the record write
/// and the change-log append leaves that record durably stored but permanently absent from the feed.
/// </para>
/// </remarks>
public interface IStateChangeFeed : IStateCapability
{
    IAsyncEnumerable<StateChangeEnvelope> ReadAsync(StateChangeCursor? from, CancellationToken cancellationToken = default);
}

/// <summary>
/// An opaque resume point for <see cref="IStateChangeFeed.ReadAsync"/>. Callers must only
/// round-trip a cursor obtained from a previous <see cref="StateChangeEnvelope.Cursor"/> — never
/// construct, compare, or compute with <see cref="Position"/> directly. It happens to wrap a
/// store's <c>GlobalPosition</c>, but that is an implementation detail providers rely on, not a
/// contract callers may depend on.
/// </summary>
public readonly record struct StateChangeCursor
{
    public StateChangeCursor(long position)
    {
        if (position <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "A change cursor position must be greater than zero.");
        }

        Position = position;
    }

    public long Position { get; }
}

/// <summary>One record from an <see cref="IStateChangeFeed"/>, paired with the cursor to resume after it.</summary>
public sealed record StateChangeEnvelope
{
    public required StateRecord Record { get; init; }

    public required StateChangeCursor Cursor { get; init; }
}

/// <summary>
/// Optional capability for a ledger store that can enumerate every distinct partition it has ever
/// recorded a write for. Unlike <see cref="IStateChangeFeed"/> (which streams individual records
/// in global order), this reports one descriptor per <see cref="StateAddress"/>, regardless of how
/// many revisions that partition has accumulated. Not resumable by cursor — callers wanting to
/// react to changes over time should use <see cref="IStateChangeFeed"/> instead; this capability
/// answers "what partitions exist", not "what changed". The order partitions are yielded in is
/// unspecified and must not be depended on — it varies by provider's backing structure. Snapshot
/// granularity is not uniform across providers either: some materialize the full listing before
/// yielding the first result (a true point-in-time snapshot), while others enumerate lazily and can
/// observe partitions written after the call began.
/// </summary>
public interface IPartitionCatalog : IStateCapability
{
    IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>One partition known to an <see cref="IPartitionCatalog"/>, with its most recent position.</summary>
public sealed record StatePartitionDescriptor
{
    public required StateAddress Address { get; init; }

    public required StateChangeCursor LastPosition { get; init; }
}

/// <summary>
/// The coherence guarantee requested from an <see cref="IDistributedCapture"/> capture.
/// <see cref="ProcessLocal"/> never leaves the calling process — it is today's default capture
/// behavior, coherent only relative to commits made through the same <see cref="IStatesman"/>
/// runtime. The two distributed levels require the underlying store(s) to back a real cross-process
/// guarantee. Passing an out-of-domain value directly to a store's
/// <see cref="IDistributedCapture.CaptureAsync"/> is a caller error
/// (<see cref="ArgumentOutOfRangeException"/>); requesting a level or scope a store or deployment
/// genuinely cannot back — the capability is missing entirely, or a specific request (like a
/// multi-store snapshot) cannot be honored — throws <see cref="NotSupportedException"/> rather than
/// silently returning a weaker guarantee.
/// </summary>
public enum StateCaptureConsistency
{
    /// <summary>Coherent only within the calling process. No store involvement beyond ordinary reads.</summary>
    ProcessLocal = 0,

    /// <summary>Every captured address reflects a committed value as of the capture, but concurrent commits during the capture are not excluded.</summary>
    ReadCommittedDistributed = 1,

    /// <summary>
    /// Every captured address reflects one consistent point in time, as if all addresses were read
    /// atomically. This guarantee only holds within a single store: a request spanning more than one
    /// store throws <see cref="NotSupportedException"/> rather than silently returning a torn read.
    /// </summary>
    SnapshotDistributed = 2,
}

/// <summary>
/// Optional capability for a ledger store that can capture several addresses with a real
/// cross-process coherence guarantee. Unlike <see cref="IStateLedgerStore.ReadLatestAsync"/> (one
/// address, no coherence guarantee across calls), this reads many addresses as of one point the
/// requested <see cref="StateCaptureConsistency"/> defines. A <see langword="null"/> value for an
/// address means that address has no record yet, exactly like <see cref="IStateLedgerStore.ReadLatestAsync"/>.
/// Implementations must return an entry — possibly <see langword="null"/> — for every requested
/// address; the caller must never need to handle a missing key. An empty address collection returns
/// an empty dictionary without contacting the store at all.
/// Returns raw <see cref="StateRecord"/> data, not a hydrated snapshot — deserializing a record into
/// a typed value requires the declaration/serializer context only the runtime has.
/// </summary>
public interface IDistributedCapture : IStateCapability
{
    ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
        IEnumerable<StateAddress> addresses,
        StateCaptureConsistency required,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for a store that keeps a replica of an authoritative ledger and can report
/// how far that replica is behind. Today only the tiered hot/cold provider implements it: lag is a
/// property of the tiering relationship, not of any single provider, so it is never forwarded
/// through <see cref="IStateCapabilityProvider"/>. The estimate is computed from each tier's
/// <see cref="IPartitionCatalog"/> and is deliberately conservative — a write that lands on the
/// authority while the estimate is in progress can make the reported lag larger than it was at
/// any single instant, never smaller, provided each tier's catalog accurately reports that tier's
/// heads and the replica is populated only through <see cref="IStateLedgerReplica.ImportAsync"/>.
/// Throws <see cref="NotSupportedException"/> when either tier cannot back the estimate, rather
/// than reporting a misleading zero.
/// </summary>
public interface IReplicationLagSource : IStateCapability
{
    ValueTask<StateReplicationLag> EstimateLagAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One replication-lag estimate from an <see cref="IReplicationLagSource"/>. Positions are the
/// store's own <c>GlobalPosition</c> values — a monotonic but <b>sparse</b> sequence (some providers
/// consume a position on a failed conditional write), so <see cref="PositionGap"/> is a distance in
/// that sequence, never a count of missing records. <see cref="PartitionsBehind"/> counts every
/// authoritative partition the replica either does not hold at all or holds at an older position;
/// a lazily-populated replica that has simply never been asked for a partition counts as behind on
/// it, by design.
/// </summary>
public sealed record StateReplicationLag
{
    /// <summary>The newest head position on the authoritative tier, or 0 if it holds no records.</summary>
    public required long AuthoritativePosition { get; init; }

    /// <summary>The newest head position on the replica tier, or 0 if it holds no records.</summary>
    public required long ReplicaPosition { get; init; }

    /// <summary>Authoritative partitions the replica lacks entirely or holds at an older position.</summary>
    public required int PartitionsBehind { get; init; }

    /// <summary>
    /// How far behind the authority's newest position the replica's newest position is, clamped at
    /// zero — a replica that somehow holds a newer position than the authority is reported as
    /// caught up on this axis rather than as negative lag.
    /// </summary>
    public long PositionGap => Math.Max(0, AuthoritativePosition - ReplicaPosition);

    /// <summary>True only when no partition is behind and the newest positions agree.</summary>
    public bool IsCaughtUp => PartitionsBehind == 0 && PositionGap == 0;
}

public interface IStateStoreResolver
{
    IStateLedgerStore Resolve(string name);
}

/// <summary>
/// Implemented by a store that wraps other stores (e.g. a tiered store) to forward capability
/// discovery to whichever wrapped store can actually back it. Consulted by
/// <see cref="StateCapabilityExtensions.TryGetCapability{TCapability}"/> only after a direct cast
/// on the store itself fails. Deliberately does not extend <see cref="IStateCapability"/> — this
/// is a forwarding mechanism, not a capability in its own right.
/// </summary>
public interface IStateCapabilityProvider
{
    bool TryGetCapability(Type capabilityType, out object? capability);
}

/// <summary>
/// Discovers optional, negotiated capabilities on a ledger store without a central registry.
/// A store implements a capability interface only when it can honestly back the guarantee it
/// implies; a store that cannot simply does not implement it.
/// </summary>
public static class StateCapabilityExtensions
{
    public static bool TryGetCapability<TCapability>(
        this IStateLedgerStore store,
        [NotNullWhen(true)] out TCapability? capability)
        where TCapability : class, IStateCapability
    {
        ArgumentNullException.ThrowIfNull(store);

        capability = store as TCapability;
        if (capability is not null)
        {
            return true;
        }

        if (store is IStateCapabilityProvider provider &&
            provider.TryGetCapability(typeof(TCapability), out object? forwarded))
        {
            capability = forwarded as TCapability;
            return capability is not null;
        }

        return false;
    }
}
